using System.Text.Json;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Accounting;

/// <summary>Mizan, bilanço, gelir tablosu, summary, settings (dönem kilidi) and form options.</summary>
public sealed partial class AccountingService
{
    private static readonly string[] RevenuePrefixes = ["60", "61", "64", "67"];

    private static (DateOnly From, DateOnly To) Range(DateOnly? fromDate, DateOnly? toDate)
    {
        var to = toDate ?? AccountingClock.Today;
        var from = fromDate ?? new DateOnly(to.Year, 1, 1);
        Guard.That(from <= to, "Başlangıç tarihi bitişten sonra olamaz");
        return (from, to);
    }

    /// <summary>Mizan; defaults to the current year up to today.</summary>
    public async Task<TrialBalanceDto> TrialBalanceAsync(DateOnly? fromDate, DateOnly? toDate, bool detailed)
    {
        var (from, to) = Range(fromDate, toDate);
        var sums = await SumsAsync(from, to);
        var accounts = await db.LedgerAccounts.AsNoTracking().Select(a => new { a.Id, a.Code, a.Name }).ToListAsync();
        var ids = accounts.GroupBy(a => a.Code).ToDictionary(g => g.Key, g => g.First().Id);
        var (rows, totals) = AccountingMath.TrialBalance(accounts.Select(a => new AccountRef(a.Code, a.Name)), sums, detailed);
        return new TrialBalanceDto(from, to, rows.Select(r => new TrialBalanceLine(ids.TryGetValue(r.Code, out var id) ? id : null, r.Code, r.Name, r.Level,
            r.PeriodDebit, r.PeriodCredit, r.TotalDebit, r.TotalCredit, r.BalanceDebit, r.BalanceCredit)).ToList(), totals);
    }

    /// <summary>Bilanço on a date (default today).</summary>
    public async Task<BalanceSheetDto> BalanceSheetAsync(DateOnly? dateValue)
    {
        var date = dateValue ?? AccountingClock.Today;
        var yearStart = await FiscalYearStartAsync(date);
        var all = await SumsAsync(yearStart, date);
        var names = await MainNamesAsync();
        var balances = MainRows(all, names, s => s.OpeningDebit + s.PeriodDebit - s.OpeningCredit - s.PeriodCredit);
        decimal Result(bool period) => all.Where(s => AccountingChart.IsResultAccount(s.Code))
            .Sum(s => period ? s.PeriodCredit - s.PeriodDebit : s.OpeningCredit - s.OpeningDebit);
        var sheet = AccountingMath.BalanceSheetOf(balances, Result(true), Result(false));
        return new BalanceSheetDto(date, sheet.Assets, sheet.Liabilities, sheet.TotalAssets, sheet.TotalLiabilities);
    }

    /// <summary>Gelir tablosu; defaults to the current year up to today.</summary>
    public async Task<IncomeStatementDto> IncomeStatementAsync(DateOnly? fromDate, DateOnly? toDate)
    {
        var (from, to) = Range(fromDate, toDate);
        var sums = await SumsAsync(from, to);
        var names = await MainNamesAsync();
        var statement = AccountingMath.IncomeStatementOf(MainRows(sums.Where(s => AccountingChart.IsResultAccount(s.Code)), names, s => s.PeriodCredit - s.PeriodDebit));
        return new IncomeStatementDto(from, to, statement.Sections, statement.NetSales, statement.GrossProfit, statement.OperatingProfit, statement.OrdinaryProfit,
            statement.ProfitBeforeTax, statement.NetProfit);
    }

    public async Task<AccountingSummaryDto> SummaryAsync(int? year)
    {
        var companyId = Ctx.RequireCompanyId();
        var today = AccountingClock.Today;
        var y = year ?? today.Year;
        var start = new DateOnly(y, 1, 1);
        var end = new DateOnly(y, 12, 31);
        var balanceDate = end < today ? end : today;
        var currency = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.BaseCurrency).FirstAsync();
        var balances = await SumsAsync(balanceDate, balanceDate);
        var pending = await PendingAsync();
        var entries = await db.JournalEntries.CountAsync(e => e.EntryDate >= start && e.EntryDate <= end);
        var accountCount = await db.LedgerAccounts.CountAsync();
        var until = await LockedUntilAsync(companyId);
        var lines = await db.JournalLines.AsNoTracking()
            .Where(l => l.Entry.Status == JournalEntryStatus.POSTED && l.Entry.EntryDate >= start && l.Entry.EntryDate <= end && (l.Account.Code.StartsWith("6") || l.Account.Code.StartsWith("7")))
            .Select(l => new { l.Entry.EntryDate.Month, l.Account.Code, l.Debit, l.Credit }).ToListAsync();
        var months = lines.GroupBy(l => (l.Month, Prefix: l.Code[..2])).Select(g => (g.Key.Month, g.Key.Prefix, Debit: g.Sum(l => l.Debit), Credit: g.Sum(l => l.Credit))).ToList();

        decimal Balance(string prefix, int sign) => balances.Where(s => s.Code.StartsWith(prefix, StringComparison.Ordinal))
            .Sum(s => (s.OpeningDebit + s.PeriodDebit - s.OpeningCredit - s.PeriodCredit) * sign);
        var monthly = Enumerable.Range(1, 12).Select(m =>
        {
            var rows = months.Where(r => r.Month == m).ToList();
            var revenue = rows.Where(r => RevenuePrefixes.Contains(r.Prefix)).Sum(r => r.Credit - r.Debit);
            var expense = rows.Where(r => !RevenuePrefixes.Contains(r.Prefix)).Sum(r => r.Debit - r.Credit);
            return new MonthResultDto(m, revenue, expense, revenue - expense);
        }).ToList();
        var netSales = months.Where(r => r.Prefix is "60" or "61").Sum(r => r.Credit - r.Debit);
        return new AccountingSummaryDto(y, currency, Balance("10", 1), Balance("12", 1), Balance("32", -1), netSales, monthly.Sum(m => m.Profit), monthly,
            pending.Count, entries, until, accountCount > 0);
    }

    public async Task<AccountingSettingsDto> SettingsAsync() => new(await LockedUntilAsync(Ctx.RequireCompanyId()));

    /// <summary>Dönem kilidi; it cannot move past today.</summary>
    public async Task<AccountingSettingsDto> UpdateSettingsAsync(UpdateAccountingSettings dto)
    {
        var companyId = Ctx.RequireCompanyId();
        Guard.That(dto.LockedUntil == null || dto.LockedUntil <= AccountingClock.Today, "Kilit tarihi ileri bir tarih olamaz");
        var json = JsonSerializer.SerializeToDocument(new { lockedUntil = dto.LockedUntil?.ToString("yyyy-MM-dd") });
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingsKey);
        if (row == null) db.Settings.Add(new Setting { CompanyId = companyId, Key = SettingsKey, Value = json });
        else row.Value = json;
        await db.SaveChangesAsync();
        return new AccountingSettingsDto(dto.LockedUntil);
    }

    /// <summary>Active accounts, partners and employees for the entry form.</summary>
    public async Task<AccountingOptionsDto> OptionsAsync()
    {
        var accounts = (await db.LedgerAccounts.AsNoTracking().Where(a => a.IsActive).Select(a => new { a.Id, a.Code, a.Name }).ToListAsync())
            .OrderBy(a => a.Code, Comparer<string>.Create(AccountingMath.CompareCodes)).Select(a => new OptionDto(a.Id, a.Code, a.Name)).ToList();
        var partners = (await db.BusinessPartners.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Title).Select(p => new OptionDto(p.Id, p.Code, p.Title)).ToListAsync());
        var employees = (await db.Employees.AsNoTracking().Where(e => e.Status == EmployeeStatus.ACTIVE).OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new OptionDto(e.Id, e.EmployeeNo, e.FirstName + " " + e.LastName)).ToListAsync());
        return new AccountingOptionsDto(accounts, partners, employees);
    }

    // ---------- Helpers ----------

    private async Task<Dictionary<string, string>> MainNamesAsync() =>
        (await db.LedgerAccounts.AsNoTracking().Where(a => a.ParentId == null).Select(a => new { a.Code, a.Name }).ToListAsync())
        .GroupBy(a => a.Code).ToDictionary(g => g.Key, g => g.First().Name);

    private async Task<DateOnly> FiscalYearStartAsync(DateOnly date)
    {
        var general = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "general");
        var month = 1;
        if (general != null && general.Value.RootElement.TryGetProperty("fiscalYearStartMonth", out var m) && m.TryGetInt32(out var parsed)) month = parsed;
        return new DateOnly(date.Month >= month ? date.Year : date.Year - 1, month, 1);
    }

    /// <summary>Posted totals per account: before <paramref name="from"/> and between <paramref name="from"/> and <paramref name="to"/>.</summary>
    private async Task<List<AccountMovement>> SumsAsync(DateOnly from, DateOnly to)
    {
        var rows = await db.JournalLines.AsNoTracking().Where(l => l.Entry.Status == JournalEntryStatus.POSTED && l.Entry.EntryDate <= to)
            .GroupBy(l => new { l.Account.Code, l.Account.Name })
            .Select(g => new
            {
                g.Key.Code, g.Key.Name,
                OpeningDebit = g.Sum(l => l.Entry.EntryDate < from ? l.Debit : 0m),
                OpeningCredit = g.Sum(l => l.Entry.EntryDate < from ? l.Credit : 0m),
                PeriodDebit = g.Sum(l => l.Entry.EntryDate >= from ? l.Debit : 0m),
                PeriodCredit = g.Sum(l => l.Entry.EntryDate >= from ? l.Credit : 0m),
            }).ToListAsync();
        return rows.Select(r => new AccountMovement(r.Code, r.Name, r.OpeningDebit, r.OpeningCredit, r.PeriodDebit, r.PeriodCredit)).ToList();
    }

    /// <summary>Rolls sub-accounts up into their three-digit main account.</summary>
    private static List<AmountRow> MainRows(IEnumerable<AccountMovement> sums, Dictionary<string, string> names, Func<AccountMovement, decimal> amount)
    {
        var rows = new Dictionary<string, AmountRow>();
        foreach (var s in sums)
        {
            var code = AccountingChart.MainCode(s.Code);
            var row = rows.GetValueOrDefault(code) ?? new AmountRow(code, names.GetValueOrDefault(code, code), 0);
            rows[code] = row with { Amount = row.Amount + amount(s) };
        }
        return rows.Values.ToList();
    }
}
