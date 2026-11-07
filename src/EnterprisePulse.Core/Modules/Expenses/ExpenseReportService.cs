using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Finance;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Projects;
using EnterprisePulse.Core.Modules.Trade;
using EnterprisePulse.Core.Modules.Workflow;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Expenses;

public sealed record ExpenseLineInput(
    Guid CategoryId, DateOnly ExpenseDate, string Description, string? Merchant, decimal? Quantity, decimal? Amount,
    decimal? VatAmount, string? CurrencyCode, decimal? ExchangeRate, Guid? ReceiptFileId);

public sealed record SaveExpenseReport(Guid? EmployeeId, Guid? ProjectId, string Title, string? Description, List<ExpenseLineInput> Lines);

public sealed record PayExpense(Guid AccountId, DateOnly? Date);

public sealed record ExpenseLineDto(
    Guid Id, int LineNo, Guid CategoryId, string CategoryName, ExpenseCategoryKind Kind, DateOnly ExpenseDate, string Description,
    string? Merchant, decimal Quantity, decimal Amount, decimal VatAmount, string CurrencyCode, decimal ExchangeRate, decimal AmountBase, bool OverLimit, bool NeedsReceipt);

public sealed record ExpenseList(List<ExpenseReportDto> Items);

public sealed record ExpenseReportDto(
    Guid Id, string Number, Guid EmployeeId, string EmployeeName, Guid? ProjectId, string? ProjectName, string Title, string? Description,
    ExpenseReportStatus Status, decimal TotalAmount, Guid? WorkflowInstanceId, DateTime? SubmittedAt, DateTime? DecidedAt,
    Guid? PaymentAccountId, DateOnly? PaymentDate, bool CanSubmit, bool CanPay, bool CanCancel, List<ExpenseLineDto> Lines);

/// <summary>Masraf formu. Onay EXPENSE_REPORT akışından gelir; ödeme kasadan çıkar ve proje maliyetine yazılır.</summary>
[ModuleService]
public sealed class ExpenseReportService(
    AppDbContext db, NumberingService numbering, WorkflowService workflow, TreasuryService treasury, TradeService trade,
    ProjectService projects, NotificationService notifications)
{
    public const string WorkflowCode = "EXPENSE_REPORT";
    public const string EntityType = "ExpenseReport";

    private CurrentUser Ctx => db.User;

    public async Task<ExpenseReportDto> CreateAsync(SaveExpenseReport dto)
    {
        var employee = await EmployeeAsync(dto.EmployeeId, creating: true);
        var report = new ExpenseReport
        {
            Number = await numbering.NextAsync("EXPENSE_REPORT"),
            EmployeeId = employee.Id,
            Title = RequireTitle(dto.Title),
            Description = Blank(dto.Description),
            ProjectId = await ProjectAsync(dto.ProjectId),
            CreatedById = Ctx.RequireUserId(),
        };
        await FillLinesAsync(report, dto.Lines);
        db.ExpenseReports.Add(report);
        await db.SaveChangesAsync();
        return await GetAsync(report.Id);
    }

    public async Task<ExpenseReportDto> UpdateAsync(Guid id, SaveExpenseReport dto)
    {
        var report = await EditableAsync(id);
        report.Title = RequireTitle(dto.Title);
        report.Description = Blank(dto.Description);
        report.ProjectId = await ProjectAsync(dto.ProjectId);
        db.ExpenseLines.RemoveRange(report.Lines);
        report.Lines.Clear();
        await FillLinesAsync(report, dto.Lines);
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<ExpenseReportDto> SubmitAsync(Guid id)
    {
        var report = await EditableAsync(id);
        Guard.That(report.Lines.Count > 0 && report.TotalAmount > 0, "Masraf formunda satır olmalı");
        var missing = await db.ExpenseLines.Where(l => l.ReportId == id && l.ReceiptFileId == null && l.Category.RequiresReceipt && l.Category.Kind == ExpenseCategoryKind.RECEIPT).AnyAsync();
        Guard.That(!missing, "Fiş zorunlu satıra fiş eklenmeden form gönderilemez");
        var hasWorkflow = await db.WorkflowDefinitions.AnyAsync(d => d.Code == WorkflowCode && d.IsActive && d.Steps.Any());
        await using var tx = await db.Database.BeginTransactionAsync();
        report.SubmittedAt = DateTime.UtcNow;
        if (hasWorkflow)
        {
            report.Status = ExpenseReportStatus.PENDING;
            var over = await db.ExpenseLines.AnyAsync(l => l.ReportId == id && l.Category.LimitAmount != null && l.AmountBase > l.Category.LimitAmount);
            var instance = await workflow.StartAsync(new StartWorkflowRequest(WorkflowCode, report.Id.ToString(),
                over ? $"{report.Number} — {report.Title} (limit aşımı)" : $"{report.Number} — {report.Title}"));
            report.WorkflowInstanceId = instance.Id;
        }
        else
        {
            report.Status = ExpenseReportStatus.APPROVED;
            report.DecidedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(id);
    }

    public async Task DecideAsync(Guid id, ExpenseReportStatus status)
    {
        var report = await db.ExpenseReports.Include(r => r.Employee).FirstOrDefaultAsync(r => r.Id == id && r.Status == ExpenseReportStatus.PENDING);
        if (report == null) return;
        report.Status = status;
        report.DecidedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        if (report.Employee.UserId is { } user && user != Ctx.UserId)
        {
            await notifications.NotifyAsync(new NotifyRequest([user], report.CompanyId, "expenses.report_decided",
                status == ExpenseReportStatus.APPROVED ? "Masraf formunuz onaylandı" : "Masraf formunuz reddedildi",
                $"{report.Number} — {report.Title}", $"/expenses/reports/{report.Id}"));
        }
    }

    public async Task<ExpenseReportDto> PayAsync(Guid id, PayExpense dto)
    {
        Guard.That(Ctx.Has("expenses:pay"), "Masraf ödeme yetkiniz yok");
        var report = await db.ExpenseReports.FirstOrDefaultAsync(r => r.Id == id) ?? throw new NotFoundAppException("Masraf formu bulunamadı");
        Guard.State(report.Status == ExpenseReportStatus.APPROVED, "Yalnızca onaylı form ödenir");
        var account = Guard.Found(await db.TreasuryAccounts.FirstOrDefaultAsync(a => a.Id == dto.AccountId && a.IsActive), "Kasa veya banka hesabı bulunamadı");
        var baseCurrency = await db.Companies.Where(c => c.Id == Ctx.CompanyId).Select(c => c.BaseCurrency).FirstAsync();
        Guard.That(account.CurrencyCode == baseCurrency, "Ödeme hesabının para birimi şirket para birimi olmalı");
        var date = dto.Date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        Guard.That(date <= DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1), "Ödeme tarihi ileri olamaz");
        await using var tx = await db.Database.BeginTransactionAsync();
        await treasury.PostAsync(account.Id, date, TreasuryDocumentType.EXPENSE_PAYMENT, report.Id, report.Number, report.Title, 0, report.TotalAmount);
        report.Status = ExpenseReportStatus.PAID;
        report.PaymentAccountId = account.Id;
        report.PaymentDate = date;
        report.PaidAt = DateTime.UtcNow;
        report.PaidById = Ctx.RequireUserId();
        if (report.ProjectId is { } projectId) await projects.AddExpenseCostAsync(projectId, report.TotalAmount);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(id);
    }

    public async Task<ExpenseReportDto> CancelAsync(Guid id, string? reason)
    {
        var report = await VisibleAsync(id);
        var mine = report.Employee.UserId == Ctx.UserId;
        Guard.That(mine || Ctx.Has("expenses:update"), "Masraf formunu yalnızca sahibi iptal edebilir");
        Guard.State(report.Status is ExpenseReportStatus.DRAFT or ExpenseReportStatus.PENDING or ExpenseReportStatus.APPROVED or ExpenseReportStatus.PAID, "Form zaten kapanmış");
        await using var tx = await db.Database.BeginTransactionAsync();
        if (report.Status == ExpenseReportStatus.PENDING && report.WorkflowInstanceId is { } instanceId)
            await workflow.CancelAsync(instanceId, reason);
        if (report.Status == ExpenseReportStatus.PAID)
        {
            await treasury.ReverseAsync(TreasuryDocumentType.EXPENSE_PAYMENT, report.Id, DateOnly.FromDateTime(DateTime.UtcNow), reason ?? "Masraf ödemesi iptal");
            if (report.ProjectId is { } projectId) await projects.AddExpenseCostAsync(projectId, -report.TotalAmount);
        }
        report.Status = ExpenseReportStatus.CANCELLED;
        report.DecidedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(id);
    }

    public async Task<ExpenseList> MineAsync()
    {
        var employeeId = await db.Employees.Where(e => e.UserId == Ctx.UserId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        if (employeeId == null) return new ExpenseList([]);
        var ids = await db.ExpenseReports.Where(r => r.EmployeeId == employeeId).OrderByDescending(r => r.CreatedAt).Select(r => r.Id).ToListAsync();
        var list = new List<ExpenseReportDto>();
        foreach (var id in ids) list.Add(await GetAsync(id, ownBypass: true));
        return new ExpenseList(list);
    }

    public async Task<ExpenseReportDto> GetAsync(Guid id) => await GetAsync(id, ownBypass: false);

    private async Task<ExpenseReportDto> GetAsync(Guid id, bool ownBypass)
    {
        var report = await db.ExpenseReports.AsNoTracking().Include(r => r.Employee).Include(r => r.Project)
            .Include(r => r.Lines).ThenInclude(l => l.Category).FirstOrDefaultAsync(r => r.Id == id)
            ?? throw new NotFoundAppException("Masraf formu bulunamadı");
        if (!ownBypass && !Ctx.Has("expenses:read") && report.Employee.UserId != Ctx.UserId)
            throw new NotFoundAppException("Masraf formu bulunamadı");
        var mine = report.Employee.UserId == Ctx.UserId;
        return new ExpenseReportDto(report.Id, report.Number, report.EmployeeId, $"{report.Employee.FirstName} {report.Employee.LastName}",
            report.ProjectId, report.Project?.Name, report.Title, report.Description, report.Status, report.TotalAmount, report.WorkflowInstanceId,
            report.SubmittedAt, report.DecidedAt, report.PaymentAccountId, report.PaymentDate,
            report.Status == ExpenseReportStatus.DRAFT && (mine || Ctx.Has("expenses:update")),
            report.Status == ExpenseReportStatus.APPROVED && Ctx.Has("expenses:pay"),
            report.Status is ExpenseReportStatus.DRAFT or ExpenseReportStatus.PENDING or ExpenseReportStatus.APPROVED or ExpenseReportStatus.PAID && (mine || Ctx.Has("expenses:update")),
            report.Lines.OrderBy(l => l.LineNo).Select(l => new ExpenseLineDto(l.Id, l.LineNo, l.CategoryId, l.Category.Name, l.Category.Kind, l.ExpenseDate,
                l.Description, l.Merchant, l.Quantity, l.Amount, l.VatAmount, l.CurrencyCode, l.ExchangeRate, l.AmountBase,
                l.Category.LimitAmount != null && l.AmountBase > l.Category.LimitAmount,
                l.Category.Kind == ExpenseCategoryKind.RECEIPT && l.Category.RequiresReceipt && l.ReceiptFileId == null)).ToList());
    }

    private async Task FillLinesAsync(ExpenseReport report, List<ExpenseLineInput> lines)
    {
        Guard.That(lines is { Count: > 0 }, "En az bir satır gerekli");
        report.TotalAmount = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var input = lines[i];
            var category = await db.ExpenseCategories.FirstOrDefaultAsync(c => c.Id == input.CategoryId && c.IsActive)
                ?? throw new NotFoundAppException($"Satır {i + 1}: masraf türü bulunamadı");
            Guard.That(!string.IsNullOrWhiteSpace(input.Description), $"Satır {i + 1}: açıklama gerekli");
            Guard.That(input.ExpenseDate <= DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1), $"Satır {i + 1}: tarih ileri olamaz");
            var quantity = input.Quantity ?? 1;
            Guard.That(quantity > 0, $"Satır {i + 1}: miktar sıfırdan büyük olmalı");
            decimal amount;
            if (category.Kind == ExpenseCategoryKind.RECEIPT)
            {
                Guard.That(input.Amount is > 0, $"Satır {i + 1}: tutar sıfırdan büyük olmalı");
                amount = input.Amount!.Value;
            }
            else
            {
                Guard.That(category.Rate is > 0, $"Satır {i + 1}: birim tutar tanımlı değil");
                amount = Math.Round(quantity * category.Rate!.Value, 2, MidpointRounding.AwayFromZero);
            }
            var vat = input.VatAmount ?? 0;
            Guard.That(vat >= 0 && vat <= amount, $"Satır {i + 1}: KDV tutarı satır tutarını aşamaz");
            var money = await trade.ResolveCurrencyAsync(input.CurrencyCode, input.ExchangeRate, input.ExpenseDate);
            if (input.ReceiptFileId != null) Guard.That(await db.FileObjects.AnyAsync(f => f.Id == input.ReceiptFileId), $"Satır {i + 1}: fiş dosyası bulunamadı");
            var line = new ExpenseLine
            {
                LineNo = i + 1, CategoryId = category.Id, ExpenseDate = input.ExpenseDate, Description = input.Description.Trim(),
                Merchant = Blank(input.Merchant), Quantity = quantity, Amount = amount, VatAmount = vat, CurrencyCode = money.CurrencyCode,
                ExchangeRate = money.ExchangeRate, AmountBase = DocumentMath.ToBase(amount, money.ExchangeRate), ReceiptFileId = input.ReceiptFileId,
            };
            report.Lines.Add(line);
            report.TotalAmount += line.AmountBase;
        }
        report.TotalAmount = Math.Round(report.TotalAmount, 2, MidpointRounding.AwayFromZero);
    }

    private async Task<ExpenseReport> EditableAsync(Guid id)
    {
        var report = await VisibleAsync(id);
        Guard.State(report.Status == ExpenseReportStatus.DRAFT, "Yalnızca taslak form düzenlenir");
        Guard.That(report.Employee.UserId == Ctx.UserId || Ctx.Has("expenses:update"), "Taslağı yalnızca sahibi düzenler");
        return report;
    }

    private async Task<ExpenseReport> VisibleAsync(Guid id)
    {
        var report = await db.ExpenseReports.Include(r => r.Employee).Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id)
            ?? throw new NotFoundAppException("Masraf formu bulunamadı");
        if (!Ctx.Has("expenses:read") && report.Employee.UserId != Ctx.UserId) throw new NotFoundAppException("Masraf formu bulunamadı");
        return report;
    }

    private async Task<Employee> EmployeeAsync(Guid? employeeId, bool creating)
    {
        Guard.That(Ctx.Has(creating ? "expenses:create" : "expenses:update") || employeeId == null, "Masraf formu yetkiniz yok");
        if (employeeId == null)
        {
            return await db.Employees.FirstOrDefaultAsync(e => e.UserId == Ctx.UserId) ?? throw new NotFoundAppException("Personel kaydınız yok");
        }
        Guard.That(Ctx.Has("expenses:create") || Ctx.Has("expenses:update"), "Başkası adına masraf formu yetkiniz yok");
        return await db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId) ?? throw new NotFoundAppException("Personel bulunamadı");
    }

    private async Task<Guid?> ProjectAsync(Guid? projectId)
    {
        if (projectId == null) return null;
        Guard.That(await db.Projects.AnyAsync(p => p.Id == projectId && p.Status != ProjectStatus.CANCELLED), "Proje bulunamadı");
        return projectId;
    }

    private static string RequireTitle(string title)
    {
        Guard.That(!string.IsNullOrWhiteSpace(title), "Başlık gerekli");
        return title.Trim();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Keeps the report in step with the approval flow.</summary>
public sealed class ExpenseReportWorkflowListener(IServiceProvider services) : IWorkflowListener
{
    public string EntityType => ExpenseReportService.EntityType;

    public Task OnFinishedAsync(WorkflowFinished e)
    {
        var reports = (ExpenseReportService)services.GetService(typeof(ExpenseReportService))!;
        if (!Guid.TryParse(e.EntityId, out var id)) return Task.CompletedTask;
        var status = e.Status switch
        {
            WorkflowStatus.APPROVED => ExpenseReportStatus.APPROVED,
            WorkflowStatus.REJECTED => ExpenseReportStatus.REJECTED,
            _ => ExpenseReportStatus.CANCELLED,
        };
        return reports.DecideAsync(id, status);
    }
}
