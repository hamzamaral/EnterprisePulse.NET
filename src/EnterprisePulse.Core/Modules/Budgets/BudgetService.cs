using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Assets;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Trade;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Budgets;

public sealed record SaveBudgetItem(string Code, string Name, BudgetItemKind Kind, BudgetSource Source, string? Description);
public sealed record SaveBudget(int Year, Guid? DepartmentId, string Name, Guid? OwnerId, string? Note);
public sealed record PlanLine(Guid ItemId, int Month, decimal Amount);
public sealed record SavePlan(List<PlanLine> Lines);
public sealed record SaveEntry(Guid ItemId, Guid? DepartmentId, DateOnly EntryDate, decimal Amount, string Description);
public sealed record BudgetRowDto(Guid ItemId, string Code, string Name, BudgetItemKind Kind, decimal PlannedYear, decimal PlannedYtd, decimal ActualYtd, decimal Utilization, string State);
public sealed record BudgetDto(Guid Id, int Year, string Name, BudgetStatus Status, Guid? DepartmentId, string? DepartmentName, Guid? OwnerId, decimal PlannedIncome, decimal PlannedExpense, decimal ActualIncome, decimal ActualExpense, List<BudgetRowDto> Rows, bool CanApprove, bool CanReopen);

/// <summary>Yıllık bütçe. Gerçekleşen MANUAL girişler ile masraf, bordro ve satış kaynaklarından toplanır.</summary>
[ModuleService]
public sealed class BudgetService(AppDbContext db, NotificationService notifications) : IScheduledJob
{
    public string Id => "budgets-daily";
    public string Cron => "0 8 * * *";

    private CurrentUser Ctx => db.User;

    public async Task RunAsync()
    {
        using var bypass = Ctx.WithoutTenant();
        foreach (var companyId in await db.Companies.Select(c => c.Id).ToListAsync())
        {
            using var _ = Ctx.ActAs(companyId, superAdmin: true);
            await CheckAlertsAsync();
        }
    }

    public async Task<BudgetItem> SaveItemAsync(SaveBudgetItem dto)
    {
        Guard.That(Ctx.Has("budgets:update"), "Bütçe kalemi yetkiniz yok");
        Guard.That(!string.IsNullOrWhiteSpace(dto.Code) && !string.IsNullOrWhiteSpace(dto.Name), "Kod ve ad gerekli");
        var row = await db.BudgetItems.FirstOrDefaultAsync(i => i.Code == dto.Code.Trim());
        if (row == null)
        {
            row = new BudgetItem { Code = dto.Code.Trim(), CategoryIds = Guid.Empty };
            db.BudgetItems.Add(row);
        }
        row.Name = dto.Name.Trim();
        row.Kind = dto.Kind;
        row.Source = dto.Source;
        row.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        row.IsActive = true;
        await db.SaveChangesAsync();
        return row;
    }

    public async Task<BudgetDto> CreateAsync(SaveBudget dto)
    {
        Guard.That(Ctx.Has("budgets:update"), "Bütçe oluşturma yetkiniz yok");
        Guard.That(!string.IsNullOrWhiteSpace(dto.Name), "Bütçe adı gerekli");
        if (dto.DepartmentId != null) Guard.That(await db.Departments.AnyAsync(d => d.Id == dto.DepartmentId), "Departman bulunamadı");
        var budget = new Budget
        {
            Year = dto.Year, DepartmentId = dto.DepartmentId, Name = dto.Name.Trim(), OwnerId = dto.OwnerId ?? Ctx.UserId,
            Note = string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note.Trim(), CreatedById = Ctx.RequireUserId(),
        };
        db.Budgets.Add(budget);
        db.BudgetEvents.Add(new BudgetEvent { BudgetId = budget.Id, ActorId = Ctx.RequireUserId(), Type = BudgetEventType.CREATED });
        await db.SaveChangesAsync();
        return await GetAsync(budget.Id);
    }

    public async Task<BudgetDto> SavePlanAsync(Guid id, SavePlan dto)
    {
        Guard.That(Ctx.Has("budgets:update"), "Plan güncelleme yetkiniz yok");
        var budget = await db.Budgets.Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == id) ?? throw new NotFoundAppException("Bütçe bulunamadı");
        Guard.State(budget.Status == BudgetStatus.DRAFT, "Onaylı bütçe planı değişmez");
        Guard.That(dto.Lines is { Count: > 0 }, "Plan satırı gerekli");
        db.BudgetLines.RemoveRange(budget.Lines);
        foreach (var line in dto.Lines)
        {
            Guard.That(line.Month is >= 1 and <= 12, "Ay 1 ile 12 arasında olmalı");
            Guard.That(line.Amount >= 0, "Plan tutarı negatif olamaz");
            Guard.That(await db.BudgetItems.AnyAsync(i => i.Id == line.ItemId && i.IsActive), "Bütçe kalemi bulunamadı");
            db.BudgetLines.Add(new BudgetLine { BudgetId = id, ItemId = line.ItemId, Month = line.Month, Amount = line.Amount });
        }
        db.BudgetEvents.Add(new BudgetEvent { BudgetId = id, ActorId = Ctx.RequireUserId(), Type = BudgetEventType.PLAN_UPDATED });
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<BudgetDto> ApproveAsync(Guid id)
    {
        Guard.That(Ctx.Has("budgets:approve"), "Bütçe onay yetkiniz yok");
        var budget = await db.Budgets.Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == id) ?? throw new NotFoundAppException("Bütçe bulunamadı");
        Guard.State(budget.Status == BudgetStatus.DRAFT, "Yalnızca taslak bütçe onaylanır");
        Guard.That(budget.Lines.Count > 0, "Plansız bütçe onaylanamaz");
        budget.Status = BudgetStatus.APPROVED;
        budget.ApprovedAt = DateTime.UtcNow;
        budget.ApprovedById = Ctx.RequireUserId();
        db.BudgetEvents.Add(new BudgetEvent { BudgetId = id, ActorId = Ctx.RequireUserId(), Type = BudgetEventType.APPROVED });
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<BudgetDto> ReopenAsync(Guid id)
    {
        Guard.That(Ctx.Has("budgets:update"), "Bütçe yeniden açma yetkiniz yok");
        var budget = await db.Budgets.Include(b => b.Alerts).FirstOrDefaultAsync(b => b.Id == id) ?? throw new NotFoundAppException("Bütçe bulunamadı");
        Guard.State(budget.Status == BudgetStatus.APPROVED, "Yalnızca onaylı bütçe yeniden açılır");
        budget.Status = BudgetStatus.DRAFT;
        budget.Revision += 1;
        budget.ApprovedAt = null;
        budget.ApprovedById = null;
        db.BudgetAlerts.RemoveRange(budget.Alerts);
        db.BudgetEvents.Add(new BudgetEvent { BudgetId = id, ActorId = Ctx.RequireUserId(), Type = BudgetEventType.REOPENED });
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task AddEntryAsync(SaveEntry dto)
    {
        Guard.That(Ctx.Has("budgets:update"), "Bütçe girişi yetkiniz yok");
        Guard.That(dto.Amount != 0, "Tutar sıfır olamaz");
        Guard.That(!string.IsNullOrWhiteSpace(dto.Description), "Açıklama gerekli");
        var item = await db.BudgetItems.FirstOrDefaultAsync(i => i.Id == dto.ItemId && i.IsActive) ?? throw new NotFoundAppException("Bütçe kalemi bulunamadı");
        Guard.That(item.Source == BudgetSource.MANUAL, "Yalnızca manuel kaleme giriş yazılır");
        db.BudgetEntries.Add(new BudgetEntry
        {
            ItemId = dto.ItemId, DepartmentId = dto.DepartmentId, EntryDate = dto.EntryDate, Amount = dto.Amount,
            Description = dto.Description.Trim(), CreatedById = Ctx.RequireUserId(),
        });
        await db.SaveChangesAsync();
    }

    public async Task<BudgetDto> GetAsync(Guid id)
    {
        var budget = await db.Budgets.AsNoTracking().Include(b => b.Department).Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == id)
            ?? throw new NotFoundAppException("Bütçe bulunamadı");
        Guard.That(Ctx.Has("budgets:read") || budget.OwnerId == Ctx.UserId, "Bütçe görme yetkiniz yok");
        var items = await db.BudgetItems.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.SortOrder).ThenBy(i => i.Code).ToListAsync();
        var actuals = await ActualsAsync(budget.Year, budget.DepartmentId);
        var elapsed = Math.Min(12, DateOnly.FromDateTime(DateTime.UtcNow).Month);
        var rows = new List<BudgetRowDto>();
        decimal plannedIncome = 0, plannedExpense = 0, actualIncome = 0, actualExpense = 0;
        foreach (var item in items)
        {
            var plannedYear = budget.Lines.Where(l => l.ItemId == item.Id).Sum(l => l.Amount);
            var plannedYtd = budget.Lines.Where(l => l.ItemId == item.Id && l.Month <= elapsed).Sum(l => l.Amount);
            actuals.TryGetValue(item.Id, out var actualYtd);
            if (plannedYear == 0 && actualYtd == 0) continue;
            var util = plannedYtd == 0 ? (actualYtd == 0 ? 0 : 999) : Math.Round(actualYtd / plannedYtd * 100m, 1, MidpointRounding.AwayFromZero);
            var state = util >= 100 ? "OVER" : util >= 90 ? "WARNING" : "ON_TRACK";
            rows.Add(new BudgetRowDto(item.Id, item.Code, item.Name, item.Kind, plannedYear, plannedYtd, actualYtd, util, state));
            if (item.Kind == BudgetItemKind.INCOME) { plannedIncome += plannedYear; actualIncome += actualYtd; }
            else { plannedExpense += plannedYear; actualExpense += actualYtd; }
        }
        return new BudgetDto(budget.Id, budget.Year, budget.Name, budget.Status, budget.DepartmentId, budget.Department?.Name, budget.OwnerId,
            plannedIncome, plannedExpense, actualIncome, actualExpense, rows, budget.Status == BudgetStatus.DRAFT && Ctx.Has("budgets:approve"),
            budget.Status == BudgetStatus.APPROVED && Ctx.Has("budgets:update"));
    }

    private async Task CheckAlertsAsync()
    {
        var year = DateOnly.FromDateTime(DateTime.UtcNow).Year;
        var budgets = await db.Budgets.Include(b => b.Lines).Include(b => b.Alerts).Where(b => b.Year == year && b.Status == BudgetStatus.APPROVED).ToListAsync();
        foreach (var budget in budgets)
        {
            var detail = await GetAsync(budget.Id);
            foreach (var row in detail.Rows.Where(r => r.Kind == BudgetItemKind.EXPENSE))
            {
                BudgetAlertLevel? level = row.Utilization >= 100 ? BudgetAlertLevel.OVER : row.Utilization >= 90 ? BudgetAlertLevel.WARNING : null;
                if (level == null) continue;
                if (budget.Alerts.Any(a => a.ItemId == row.ItemId && a.Level == level)) continue;
                db.BudgetAlerts.Add(new BudgetAlert { BudgetId = budget.Id, ItemId = row.ItemId, Level = level.Value });
                db.BudgetEvents.Add(new BudgetEvent { BudgetId = budget.Id, Type = BudgetEventType.ALERT_SENT, Note = $"{row.Code} {level}" });
                if (budget.OwnerId is { } owner)
                    await notifications.NotifyAsync(new NotifyRequest([owner], budget.CompanyId, "budgets.alert",
                        $"Bütçe uyarısı: {budget.Name}", $"{row.Name} kullanımı %{row.Utilization:0.#}", $"/budgets/{budget.Id}", true));
            }
        }
        await db.SaveChangesAsync();
    }

    private async Task<Dictionary<Guid, decimal>> ActualsAsync(int year, Guid? departmentId)
    {
        var start = new DateOnly(year, 1, 1);
        var end = new DateOnly(year, 12, 31);
        var items = await db.BudgetItems.AsNoTracking().Where(i => i.IsActive).ToListAsync();
        var map = items.ToDictionary(i => i.Id, _ => 0m);
        foreach (var item in items)
        {
            decimal amount = item.Source switch
            {
                BudgetSource.MANUAL => await db.BudgetEntries.Where(e => e.ItemId == item.Id && e.EntryDate >= start && e.EntryDate <= end
                    && (departmentId == null ? e.DepartmentId == null : e.DepartmentId == departmentId)).SumAsync(e => (decimal?)e.Amount) ?? 0,
                BudgetSource.EXPENSES => await db.ExpenseReports.Where(r => r.Status == ExpenseReportStatus.PAID && r.PaymentDate != null && r.PaymentDate >= start && r.PaymentDate <= end).SumAsync(r => (decimal?)r.TotalAmount) ?? 0,
                BudgetSource.PAYROLL => await db.PayrollPeriods.Where(p => p.Year == year && (p.Status == PayrollPeriodStatus.APPROVED || p.Status == PayrollPeriodStatus.PAID)).SumAsync(p => (decimal?)p.EmployerCostTotal) ?? 0,
                BudgetSource.SALES => (await db.SalesInvoices.Where(i => i.Status == InvoiceStatus.ISSUED && i.InvoiceDate >= start && i.InvoiceDate <= end).ToListAsync())
                    .Sum(i => DocumentMath.ToBase(i.GrandTotal - i.VatTotal, i.ExchangeRate)),
                BudgetSource.PURCHASES => (await db.PurchaseInvoices.Where(i => i.Status == InvoiceStatus.ISSUED && i.InvoiceDate >= start && i.InvoiceDate <= end).ToListAsync())
                    .Sum(i => DocumentMath.ToBase(i.GrandTotal - i.VatTotal, i.ExchangeRate)),
                _ => 0,
            };
            map[item.Id] = amount;
        }
        return map;
    }
}

public sealed class BudgetsHook() : ManagedRecordHook("budgets", "Bütçeler bütçe ekranından yönetilir");
public sealed class BudgetLinesHook() : ManagedRecordHook("budget-lines", "Plan satırları bütçe ekranından yazılır");
