using System.Text.Json;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Assets;
using EnterprisePulse.Core.Modules.Finance;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Payroll;

public sealed record TaxBracket(decimal? UpTo, decimal Rate);

public sealed record SavePayrollParameter(
    int Year, decimal MinimumWage, decimal SgkCeiling, decimal SgkEmployeeRate, decimal UnemploymentEmployeeRate,
    decimal SgkEmployerRate, decimal UnemploymentEmployerRate, decimal StampTaxRate, decimal OvertimeRate, int MonthlyHours, List<TaxBracket> TaxBrackets);

public sealed record SaveSalary(Guid EmployeeId, DateOnly EffectiveFrom, decimal GrossSalary, string? Note);

public sealed record SalaryDto(Guid Id, Guid EmployeeId, decimal GrossSalary);

public sealed record PayslipAdjust(decimal? OvertimeHours, int? MissingDays, string? EarningDescription, decimal? EarningAmount, string? DeductionDescription, decimal? DeductionAmount);

public sealed record PayPayroll(Guid AccountId, DateOnly? Date);

public sealed record PayslipDto(
    Guid Id, Guid EmployeeId, string EmployeeNo, string EmployeeName, decimal MonthlySalary, int PayDays, decimal OvertimeHours,
    decimal GrossPay, decimal SgkEmployee, decimal UnemploymentEmployee, decimal IncomeTax, decimal IncomeTaxExemption,
    decimal StampTax, decimal StampTaxExemption, decimal OtherDeductions, decimal NetPay, decimal EmployerCost);

public sealed record MyPayslip(Guid Id, Guid PeriodId, string Number, int Year, int Month, decimal GrossPay, decimal NetPay, PayrollPeriodStatus Status);
public sealed record PayrollMine(List<MyPayslip> Items);

public sealed record PayrollPeriodDto(
    Guid Id, string Number, int Year, int Month, PayrollPeriodStatus Status, int EmployeeCount, decimal GrossTotal, decimal NetTotal,
    decimal SgkTotal, decimal TaxTotal, decimal EmployerCostTotal, bool CanApprove, bool CanPay, List<PayslipDto> Payslips);

/// <summary>Aylık bordro. SGK tavanı, kümülatif gelir vergisi ve asgari ücret istisnası dönem hesabına girer.</summary>
[ModuleService]
public sealed class PayrollService(AppDbContext db, NumberingService numbering, TreasuryService treasury)
{
    private static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public async Task<PayrollParameter> SaveParameterAsync(SavePayrollParameter dto)
    {
        Guard.That(db.User.Has("payroll:update"), "Bordro parametresi yetkiniz yok");
        Guard.That(dto.TaxBrackets is { Count: > 0 } && dto.TaxBrackets.All(b => b.Rate >= 0 && b.Rate < 1), "Vergi dilimleri geçersiz");
        Guard.That(dto.MinimumWage > 0 && dto.SgkCeiling >= dto.MinimumWage && dto.MonthlyHours > 0, "Bordro parametreleri geçersiz");
        var row = await db.PayrollParameters.FirstOrDefaultAsync(p => p.Year == dto.Year);
        if (row == null)
        {
            row = new PayrollParameter { Year = dto.Year };
            db.PayrollParameters.Add(row);
        }
        row.MinimumWage = dto.MinimumWage;
        row.SgkCeiling = dto.SgkCeiling;
        row.SgkEmployeeRate = dto.SgkEmployeeRate;
        row.UnemploymentEmployeeRate = dto.UnemploymentEmployeeRate;
        row.SgkEmployerRate = dto.SgkEmployerRate;
        row.UnemploymentEmployerRate = dto.UnemploymentEmployerRate;
        row.StampTaxRate = dto.StampTaxRate;
        row.OvertimeRate = dto.OvertimeRate;
        row.MonthlyHours = dto.MonthlyHours;
        row.TaxBrackets = JsonSerializer.SerializeToDocument(dto.TaxBrackets, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await db.SaveChangesAsync();
        return row;
    }

    public async Task<SalaryDto> SaveSalaryAsync(SaveSalary dto)
    {
        Guard.That(db.User.Has("payroll:update"), "Ücret kaydı yetkiniz yok");
        Guard.That(dto.GrossSalary > 0, "Brüt ücret sıfırdan büyük olmalı");
        Guard.That(await db.Employees.AnyAsync(e => e.Id == dto.EmployeeId), "Personel bulunamadı");
        var row = await db.SalaryRecords.FirstOrDefaultAsync(s => s.EmployeeId == dto.EmployeeId && s.EffectiveFrom == dto.EffectiveFrom);
        if (row == null)
        {
            row = new SalaryRecord { EmployeeId = dto.EmployeeId, EffectiveFrom = dto.EffectiveFrom, CreatedById = db.User.RequireUserId() };
            db.SalaryRecords.Add(row);
        }
        row.GrossSalary = dto.GrossSalary;
        row.Note = string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note.Trim();
        await db.SaveChangesAsync();
        return new SalaryDto(row.Id, row.EmployeeId, row.GrossSalary);
    }

    public async Task<PayrollPeriodDto> OpenAsync(int year, int month)
    {
        Guard.That(db.User.Has("payroll:create"), "Bordro dönemi açma yetkiniz yok");
        Guard.That(month is >= 1 and <= 12, "Ay 1 ile 12 arasında olmalı");
        Guard.State(!await db.PayrollPeriods.AnyAsync(p => p.Year == year && p.Month == month), "Bu ayın bordrosu zaten var");
        Guard.That(await db.PayrollParameters.AnyAsync(p => p.Year == year), "Bu yılın bordro parametresi yok");
        var period = new PayrollPeriod { Number = await numbering.NextAsync("PAYROLL"), Year = year, Month = month, CreatedById = db.User.RequireUserId() };
        db.PayrollPeriods.Add(period);
        await db.SaveChangesAsync();
        await CalculateAsync(period.Id, null);
        return await GetAsync(period.Id);
    }

    public async Task<PayrollPeriodDto> AdjustAsync(Guid payslipId, PayslipAdjust dto)
    {
        var slip = await db.Payslips.Include(s => s.Period).Include(s => s.Items).FirstOrDefaultAsync(s => s.Id == payslipId)
            ?? throw new NotFoundAppException("Bordro pusulası bulunamadı");
        Guard.State(slip.Period.Status == PayrollPeriodStatus.DRAFT, "Onaylı bordro değişmez");
        Guard.That(db.User.Has("payroll:update"), "Bordro güncelleme yetkiniz yok");
        if (dto.OvertimeHours != null) { Guard.That(dto.OvertimeHours >= 0, "Fazla mesai negatif olamaz"); slip.OvertimeHours = dto.OvertimeHours.Value; }
        if (dto.MissingDays != null) { Guard.That(dto.MissingDays >= 0, "Eksik gün negatif olamaz"); slip.MissingDays = dto.MissingDays.Value; }
        if (dto.EarningAmount is > 0) slip.Items.Add(new PayslipItem { Kind = PayslipItemKind.EARNING, Description = dto.EarningDescription ?? "Ek kazanç", Amount = dto.EarningAmount.Value });
        if (dto.DeductionAmount is > 0) slip.Items.Add(new PayslipItem { Kind = PayslipItemKind.DEDUCTION, Description = dto.DeductionDescription ?? "Kesinti", Amount = dto.DeductionAmount.Value });
        await db.SaveChangesAsync();
        await CalculateAsync(slip.PeriodId, slip.Id);
        return await GetAsync(slip.PeriodId);
    }

    public async Task<PayrollPeriodDto> ApproveAsync(Guid id)
    {
        Guard.That(db.User.Has("payroll:approve"), "Bordro onay yetkiniz yok");
        var period = await db.PayrollPeriods.Include(p => p.Payslips).FirstOrDefaultAsync(p => p.Id == id) ?? throw new NotFoundAppException("Bordro dönemi bulunamadı");
        Guard.State(period.Status == PayrollPeriodStatus.DRAFT, "Yalnızca taslak bordro onaylanır");
        Guard.That(period.Payslips.Count > 0, "Pusulasız bordro onaylanamaz");
        period.Status = PayrollPeriodStatus.APPROVED;
        period.ApprovedAt = DateTime.UtcNow;
        period.ApprovedById = db.User.RequireUserId();
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<PayrollPeriodDto> PayAsync(Guid id, PayPayroll dto)
    {
        Guard.That(db.User.Has("payroll:pay"), "Bordro ödeme yetkiniz yok");
        var period = await db.PayrollPeriods.FirstOrDefaultAsync(p => p.Id == id) ?? throw new NotFoundAppException("Bordro dönemi bulunamadı");
        Guard.State(period.Status == PayrollPeriodStatus.APPROVED, "Yalnızca onaylı bordro ödenir");
        var account = Guard.Found(await db.TreasuryAccounts.FirstOrDefaultAsync(a => a.Id == dto.AccountId && a.IsActive), "Kasa veya banka hesabı bulunamadı");
        var baseCurrency = await db.Companies.Where(c => c.Id == db.User.CompanyId).Select(c => c.BaseCurrency).FirstAsync();
        Guard.That(account.CurrencyCode == baseCurrency, "Ödeme hesabının para birimi şirket para birimi olmalı");
        var date = dto.Date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        await using var tx = await db.Database.BeginTransactionAsync();
        await treasury.PostAsync(account.Id, date, TreasuryDocumentType.PAYROLL_PAYMENT, period.Id, period.Number, $"{period.Year}/{period.Month:00} bordro", 0, period.NetTotal);
        period.Status = PayrollPeriodStatus.PAID;
        period.PaymentAccountId = account.Id;
        period.PaymentDate = date;
        period.PaidAt = DateTime.UtcNow;
        period.PaidById = db.User.RequireUserId();
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(id);
    }

    public async Task<PayrollMine> MineAsync()
    {
        var employeeId = await db.Employees.Where(e => e.UserId == db.User.UserId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        if (employeeId == null) return new PayrollMine([]);
        var slips = await db.Payslips.AsNoTracking().Include(s => s.Period)
            .Where(s => s.EmployeeId == employeeId && (s.Period.Status == PayrollPeriodStatus.APPROVED || s.Period.Status == PayrollPeriodStatus.PAID))
            .OrderByDescending(s => s.Period.Year).ThenByDescending(s => s.Period.Month).ToListAsync();
        return new PayrollMine(slips.Select(s => new MyPayslip(s.Id, s.PeriodId, s.Period.Number, s.Period.Year, s.Period.Month, s.GrossPay, s.NetPay, s.Period.Status)).ToList());
    }

    public async Task<PayrollPeriodDto> GetAsync(Guid id)
    {
        Guard.That(db.User.Has("payroll:read"), "Bordro görme yetkiniz yok");
        var period = await db.PayrollPeriods.AsNoTracking().Include(p => p.Payslips).FirstOrDefaultAsync(p => p.Id == id)
            ?? throw new NotFoundAppException("Bordro dönemi bulunamadı");
        return Map(period);
    }

    private async Task CalculateAsync(Guid periodId, Guid? onlySlipId)
    {
        var period = await db.PayrollPeriods.Include(p => p.Payslips).ThenInclude(s => s.Items).FirstAsync(p => p.Id == periodId);
        var parameter = await db.PayrollParameters.FirstAsync(p => p.Year == period.Year);
        var brackets = ReadBrackets(parameter.TaxBrackets);
        var start = new DateOnly(period.Year, period.Month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        if (period.Payslips.Count == 0)
        {
            var employees = await db.Employees.Include(e => e.Department).Include(e => e.Position)
                .Where(e => e.Status != EmployeeStatus.TERMINATED || e.TerminationDate >= start).Where(e => e.HireDate <= end).ToListAsync();
            foreach (var employee in employees)
            {
                var salary = await db.SalaryRecords.Where(s => s.EmployeeId == employee.Id && s.EffectiveFrom <= end).OrderByDescending(s => s.EffectiveFrom).Select(s => (decimal?)s.GrossSalary).FirstOrDefaultAsync();
                if (salary == null) continue;
                var employed = EmployedDays(employee.HireDate, employee.TerminationDate, start, end);
                if (employed <= 0) continue;
                period.Payslips.Add(new Payslip
                {
                    EmployeeId = employee.Id, EmployeeNo = employee.EmployeeNo, EmployeeName = $"{employee.FirstName} {employee.LastName}",
                    DepartmentName = employee.Department?.Name, PositionName = employee.Position?.Name, MonthlySalary = salary.Value, EmployedDays = employed,
                });
            }
        }
        foreach (var slip in period.Payslips.Where(s => onlySlipId == null || s.Id == onlySlipId))
        {
            var payDays = slip.EmployedDays - slip.MissingDays;
            Guard.That(payDays >= 0, "Eksik gün çalışma gününü aşamaz");
            slip.PayDays = payDays;
            slip.ExtraEarnings = slip.Items.Where(i => i.Kind == PayslipItemKind.EARNING).Sum(i => i.Amount);
            slip.OtherDeductions = slip.Items.Where(i => i.Kind == PayslipItemKind.DEDUCTION).Sum(i => i.Amount);
            slip.BasePay = Money(slip.MonthlySalary * payDays / 30m);
            slip.OvertimePay = Money(slip.MonthlySalary / parameter.MonthlyHours * slip.OvertimeHours * parameter.OvertimeRate);
            slip.GrossPay = slip.BasePay + slip.OvertimePay + slip.ExtraEarnings;
            var floor = Money(parameter.MinimumWage * payDays / 30m);
            var cap = Money(parameter.SgkCeiling * payDays / 30m);
            slip.SgkBase = payDays == 0 ? 0 : Math.Min(cap, Math.Max(slip.GrossPay, floor));
            slip.SgkEmployee = Money(slip.SgkBase * parameter.SgkEmployeeRate);
            slip.UnemploymentEmployee = Money(slip.SgkBase * parameter.UnemploymentEmployeeRate);
            slip.SgkEmployer = Money(slip.SgkBase * parameter.SgkEmployerRate);
            slip.UnemploymentEmployer = Money(slip.SgkBase * parameter.UnemploymentEmployerRate);
            slip.IncomeTaxBase = Math.Max(0, slip.GrossPay - slip.SgkEmployee - slip.UnemploymentEmployee);
            var prior = await db.Payslips.Where(s => s.EmployeeId == slip.EmployeeId && s.Period.Year == period.Year && s.Id != slip.Id
                    && (s.Period.Status != PayrollPeriodStatus.DRAFT || s.Period.Imported) && (s.Period.Year < period.Year || s.Period.Month < period.Month))
                .SumAsync(s => (decimal?)s.IncomeTaxBase) ?? 0;
            slip.CumulativeTaxBase = prior + slip.IncomeTaxBase;
            slip.IncomeTax = Math.Max(0, BracketTax(slip.CumulativeTaxBase, brackets) - BracketTax(prior, brackets));
            var exemptionBase = Math.Max(0, floor - Money(floor * parameter.SgkEmployeeRate) - Money(floor * parameter.UnemploymentEmployeeRate));
            slip.IncomeTaxExemption = Math.Min(slip.IncomeTax, BracketTax(exemptionBase, brackets));
            slip.StampTax = Money(slip.GrossPay * parameter.StampTaxRate);
            slip.StampTaxExemption = Math.Min(slip.StampTax, Money(floor * parameter.StampTaxRate));
            slip.NetPay = slip.GrossPay - slip.SgkEmployee - slip.UnemploymentEmployee - (slip.IncomeTax - slip.IncomeTaxExemption)
                - (slip.StampTax - slip.StampTaxExemption) - slip.OtherDeductions;
            Guard.That(slip.NetPay >= 0, $"{slip.EmployeeName} net ücreti negatife düşüyor");
            slip.EmployerCost = slip.GrossPay + slip.SgkEmployer + slip.UnemploymentEmployer;
        }
        period.EmployeeCount = period.Payslips.Count;
        period.GrossTotal = period.Payslips.Sum(s => s.GrossPay);
        period.NetTotal = period.Payslips.Sum(s => s.NetPay);
        period.SgkTotal = period.Payslips.Sum(s => s.SgkEmployee + s.UnemploymentEmployee + s.SgkEmployer + s.UnemploymentEmployer);
        period.TaxTotal = period.Payslips.Sum(s => (s.IncomeTax - s.IncomeTaxExemption) + (s.StampTax - s.StampTaxExemption));
        period.DeductionTotal = period.Payslips.Sum(s => s.OtherDeductions);
        period.EmployerCostTotal = period.Payslips.Sum(s => s.EmployerCost);
        await db.SaveChangesAsync();
    }

    private static int EmployedDays(DateOnly hire, DateOnly? termination, DateOnly start, DateOnly end)
    {
        if (hire > end || termination < start) return 0;
        var from = hire < start ? 1 : hire.Day;
        var to = termination == null || termination > end ? 30 : Math.Min(30, termination.Value.Day);
        return Math.Max(0, to - from + 1);
    }

    private static decimal BracketTax(decimal amount, List<(decimal? UpTo, decimal Rate)> brackets)
    {
        decimal tax = 0, previous = 0;
        foreach (var bracket in brackets)
        {
            var slice = bracket.UpTo == null ? amount : Math.Min(amount, bracket.UpTo.Value);
            if (slice > previous) tax += (slice - previous) * bracket.Rate;
            previous = slice;
            if (bracket.UpTo == null || amount <= bracket.UpTo) break;
        }
        return Money(tax);
    }

    private static List<(decimal? UpTo, decimal Rate)> ReadBrackets(JsonDocument document)
    {
        var list = new List<(decimal?, decimal)>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            decimal? upTo = item.TryGetProperty("upTo", out var cap) && cap.ValueKind != JsonValueKind.Null ? cap.GetDecimal() : null;
            list.Add((upTo, item.GetProperty("rate").GetDecimal()));
        }
        return list;
    }

    private static PayrollPeriodDto Map(PayrollPeriod period) => new(
        period.Id, period.Number, period.Year, period.Month, period.Status, period.EmployeeCount, period.GrossTotal, period.NetTotal,
        period.SgkTotal, period.TaxTotal, period.EmployerCostTotal, period.Status == PayrollPeriodStatus.DRAFT, period.Status == PayrollPeriodStatus.APPROVED,
        period.Payslips.OrderBy(s => s.EmployeeName).Select(s => new PayslipDto(s.Id, s.EmployeeId, s.EmployeeNo, s.EmployeeName, s.MonthlySalary, s.PayDays, s.OvertimeHours,
            s.GrossPay, s.SgkEmployee, s.UnemploymentEmployee, s.IncomeTax, s.IncomeTaxExemption, s.StampTax, s.StampTaxExemption, s.OtherDeductions, s.NetPay, s.EmployerCost)).ToList());
}

public sealed class PayrollPeriodsHook() : ManagedRecordHook("payroll-periods", "Bordro dönemleri bordro ekranından yönetilir");
public sealed class PayslipsHook() : ManagedRecordHook("payslips", "Pusulalar bordro hesabıyla oluşur");
public sealed class PayslipItemsHook() : ManagedRecordHook("payslip-items", "Pusula kalemleri bordro ekranından eklenir");
