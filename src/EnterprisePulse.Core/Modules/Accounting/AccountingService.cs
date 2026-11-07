using System.Text.Json;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Accounting;

public sealed record SaveJournalLine(Guid AccountId, Guid? PartnerId, Guid? EmployeeId, string? Description, decimal Debit, decimal Credit);
public sealed record SaveJournalEntry(DateOnly EntryDate, string Description, List<SaveJournalLine> Lines);
public sealed record ReverseJournalEntry(DateOnly Date, string? Note);
public sealed record CreateLedgerAccount(string Code, string Name);
public sealed record UpdateLedgerAccount(string? Name, bool? IsActive);
public sealed record UpdateAccountingSettings(DateOnly? LockedUntil);

public sealed record LedgerAccountDto(
    Guid Id, string Code, string Name, LedgerAccountType Type, Guid? ParentId, int Level, bool IsSystem, bool IsActive,
    decimal Debit, decimal Credit, decimal Balance, bool HasEntries, int ChildCount);

public sealed record LedgerRowDto(
    Guid EntryId, string EntryNumber, DateOnly EntryDate, JournalSource Source, string? SourceNumber, string Description, string? LineDescription,
    string AccountCode, string? PartnerName, string? EmployeeName, decimal Debit, decimal Credit, decimal Balance);

public sealed record AccountLedgerDto(
    LedgerAccountDto Account, DateOnly From, DateOnly To, decimal Opening, List<LedgerRowDto> Rows, decimal TotalDebit, decimal TotalCredit, decimal Closing);

public sealed record JournalLineDto(
    Guid Id, int LineNo, Guid AccountId, string AccountCode, string AccountName, Guid? PartnerId, string? PartnerName, Guid? EmployeeId,
    string? EmployeeName, string? Description, decimal Debit, decimal Credit);

public sealed record EntryRef(Guid Id, string Number);

public sealed record JournalEntryDetailDto(
    Guid Id, string Number, DateOnly EntryDate, JournalEntryStatus Status, JournalSource Source, Guid? SourceId, string? SourceNumber, string Description,
    decimal Total, bool IsReversal, bool IsReversed, string CreatedByName, DateTime? PostedAt, Guid? DocumentId, List<JournalLineDto> Lines,
    EntryRef? Reverses, EntryRef? Reversal, bool Locked, bool CanEdit, bool CanPost, bool CanReverse, bool CanDelete);

public sealed record PendingPostingDto(
    JournalSource Source, Guid SourceId, string SourceNumber, string Description, DateOnly Date, decimal Amount, string Action, bool Locked);

public sealed record PostingRunResultDto(int Posted, int Reversed, int Skipped);

public sealed record TrialBalanceDto(DateOnly From, DateOnly To, List<TrialBalanceLine> Rows, TrialBalanceTotals Totals);

public sealed record TrialBalanceLine(
    Guid? AccountId, string Code, string Name, int Level, decimal PeriodDebit, decimal PeriodCredit, decimal TotalDebit, decimal TotalCredit,
    decimal BalanceDebit, decimal BalanceCredit);

public sealed record BalanceSheetDto(DateOnly Date, List<StatementClass> Assets, List<StatementClass> Liabilities, decimal TotalAssets, decimal TotalLiabilities);

public sealed record IncomeStatementDto(
    DateOnly From, DateOnly To, List<IncomeSection> Sections, decimal NetSales, decimal GrossProfit, decimal OperatingProfit, decimal OrdinaryProfit,
    decimal ProfitBeforeTax, decimal NetProfit);

public sealed record MonthResultDto(int Month, decimal Revenue, decimal Expense, decimal Profit);

public sealed record AccountingSummaryDto(
    int Year, string CurrencyCode, decimal Cash, decimal Receivables, decimal Payables, decimal NetSales, decimal NetProfit, List<MonthResultDto> Months,
    int PendingCount, int EntryCount, DateOnly? LockedUntil, bool ChartInstalled);

public sealed record AccountingSettingsDto(DateOnly? LockedUntil);

public sealed record OptionDto(Guid Id, string Code, string Name);

public sealed record AccountingOptionsDto(List<OptionDto> Accounts, List<OptionDto> Partners, List<OptionDto> Employees);

public static class AccountingClock
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    /// <summary>Calendar day of a UTC timestamp in Istanbul.</summary>
    public static DateOnly LocalDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone));

    public static DateOnly Today => LocalDate(DateTime.UtcNow);
}

/// <summary>
/// Genel muhasebe: chart of accounts (Tekdüzen Hesap Planı), manual journal entries, automatic postings from source documents,
/// period lock and the statements (mizan, bilanço, gelir tablosu). Posting and reports live in partial files.
/// </summary>
[ModuleService]
public sealed partial class AccountingService(AppDbContext db, NumberingService numbering) : IScheduledJob
{
    public const string SettingsKey = "accounting";
    public const string JournalSequence = "JOURNAL_ENTRY";

    public string Id => "accounting-posting";
    public string Cron => "30 2 * * *";

    private CurrentUser Ctx => db.User;

    // ---------- Period lock ----------

    public async Task<DateOnly?> LockedUntilAsync(Guid companyId)
    {
        var row = await db.Settings.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Key == SettingsKey);
        if (row != null && row.Value.RootElement.TryGetProperty("lockedUntil", out var value) && value.ValueKind == JsonValueKind.String
            && DateOnly.TryParse(value.GetString(), out var date)) return date;
        return null;
    }

    public static bool IsLocked(DateOnly date, DateOnly? until) => until != null && date <= until;

    private static void AssertOpenPeriod(DateOnly date, DateOnly? until) =>
        Guard.That(!IsLocked(date, until), $"Dönem {until:yyyy-MM-dd} tarihine kadar kilitli; daha ileri bir tarih seçin");

    /// <summary>Serializes posting runs and manual postings of one company for the rest of the transaction.</summary>
    private async Task LockAccountingAsync(Guid companyId)
    {
        var key = $"accounting:{companyId}";
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM (SELECT pg_advisory_xact_lock(hashtext({key}))) AS l");
    }

    // ---------- Chart of accounts ----------

    /// <summary>Adds the missing accounts of the standard chart; existing accounts keep their names.</summary>
    public async Task<int> InstallChartAsync(Guid? companyId = null)
    {
        var company = companyId ?? Ctx.RequireCompanyId();
        var chart = AccountingChart.Default();
        var existing = await db.LedgerAccounts.IgnoreQueryFilters().Where(a => a.CompanyId == company).ToDictionaryAsync(a => a.Code);
        var created = 0;
        foreach (var account in chart)
        {
            if (existing.TryGetValue(account.Code, out var row))
            {
                if (account.IsSystem && (!row.IsSystem || !row.IsActive))
                {
                    row.IsSystem = true;
                    row.IsActive = true;
                }
                continue;
            }
            db.LedgerAccounts.Add(new LedgerAccount { CompanyId = company, Code = account.Code, Name = account.Name, Type = account.Type, IsSystem = account.IsSystem });
            created++;
        }
        await db.SaveChangesAsync();
        return created;
    }

    public async Task<List<LedgerAccountDto>> ListAccountsAsync()
    {
        var accounts = await db.LedgerAccounts.AsNoTracking().ToListAsync();
        var sums = await AccountSumsAsync();
        accounts.Sort((a, b) => AccountingMath.CompareCodes(a.Code, b.Code));
        return accounts.Select(a => ToDto(a, accounts, sums)).ToList();
    }

    public async Task<LedgerAccountDto> GetAccountAsync(Guid id)
    {
        var list = await ListAccountsAsync();
        return list.FirstOrDefault(a => a.Id == id) ?? throw new NotFoundAppException("Hesap bulunamadı");
    }

    /// <summary>Sub-accounts inherit the type of their parent; main accounts take it from the class.</summary>
    public async Task<LedgerAccountDto> CreateAccountAsync(CreateLedgerAccount dto)
    {
        var code = (dto.Code ?? "").Trim();
        Guard.That(AccountingChart.IsValidCode(code), "Hesap kodu 100 veya 100.01 biçiminde olmalı");
        Guard.That(!string.IsNullOrWhiteSpace(dto.Name), "Hesap adı gerekli");
        var dot = code.LastIndexOf('.');
        LedgerAccount? parent = null;
        if (dot > 0)
        {
            var parentCode = code[..dot];
            parent = await db.LedgerAccounts.FirstOrDefaultAsync(a => a.Code == parentCode);
            Guard.That(parent != null, $"{parentCode} hesabı yok");
        }
        Guard.State(!await db.LedgerAccounts.AnyAsync(a => a.Code == code), $"{code} hesabı zaten var");
        var row = new LedgerAccount { Code = code, Name = dto.Name.Trim(), Type = parent?.Type ?? AccountingChart.TypeOf(code), ParentId = parent?.Id };
        db.LedgerAccounts.Add(row);
        await db.SaveChangesAsync();
        return await GetAccountAsync(row.Id);
    }

    public async Task<LedgerAccountDto> UpdateAccountAsync(Guid id, UpdateLedgerAccount dto)
    {
        var account = await db.LedgerAccounts.FirstOrDefaultAsync(a => a.Id == id) ?? throw new NotFoundAppException("Hesap bulunamadı");
        Guard.State(!(dto.IsActive == false && account.IsSystem), "Sistem hesapları otomatik kayıtlarda kullanılır");
        if (!string.IsNullOrWhiteSpace(dto.Name)) account.Name = dto.Name.Trim();
        if (dto.IsActive != null) account.IsActive = dto.IsActive.Value;
        await db.SaveChangesAsync();
        return await GetAccountAsync(id);
    }

    /// <summary>Only unused accounts without sub-accounts; the others are deactivated instead.</summary>
    public async Task RemoveAccountAsync(Guid id)
    {
        var account = await db.LedgerAccounts.FirstOrDefaultAsync(a => a.Id == id) ?? throw new NotFoundAppException("Hesap bulunamadı");
        Guard.State(!account.IsSystem, "Sistem hesapları silinemez");
        var children = await db.LedgerAccounts.CountAsync(a => a.ParentId == id);
        var lines = await db.JournalLines.CountAsync(l => l.AccountId == id);
        Guard.State(children == 0 && lines == 0, "Hesabın alt hesabı veya kaydı var; pasifleştirin");
        db.LedgerAccounts.Remove(account);
        await db.SaveChangesAsync();
    }

    /// <summary>Muavin defter: posted lines of the account and its sub-accounts with a running balance.</summary>
    public async Task<AccountLedgerDto> LedgerAsync(Guid id, DateOnly? fromDate, DateOnly? toDate, Guid? partnerId)
    {
        var account = await GetAccountAsync(id);
        var to = toDate ?? AccountingClock.Today;
        var from = fromDate ?? new DateOnly(to.Year, 1, 1);
        Guard.That(from <= to, "Başlangıç tarihi bitişten sonra olamaz");
        var accountIds = (await db.LedgerAccounts.AsNoTracking().Select(a => new { a.Id, a.Code }).ToListAsync())
            .Where(a => AccountingMath.Covers(account.Code, a.Code)).Select(a => a.Id).ToList();

        var posted = db.JournalLines.AsNoTracking().Where(l => accountIds.Contains(l.AccountId) && l.Entry.Status == JournalEntryStatus.POSTED
            && (partnerId == null || l.PartnerId == partnerId));
        var before = await posted.Where(l => l.Entry.EntryDate < from).GroupBy(_ => 1)
            .Select(g => new { Debit = g.Sum(l => l.Debit), Credit = g.Sum(l => l.Credit) }).FirstOrDefaultAsync();
        var lines = await posted.Where(l => l.Entry.EntryDate >= from && l.Entry.EntryDate <= to)
            .Include(l => l.Entry).Include(l => l.Account).Include(l => l.Partner).Include(l => l.Employee)
            .OrderBy(l => l.Entry.EntryDate).ThenBy(l => l.Entry.Number).ThenBy(l => l.LineNo).ToListAsync();
        var opening = (before?.Debit ?? 0) - (before?.Credit ?? 0);
        var balance = opening;
        decimal totalDebit = 0, totalCredit = 0;
        var rows = new List<LedgerRowDto>();
        foreach (var l in lines)
        {
            balance += l.Debit - l.Credit;
            totalDebit += l.Debit;
            totalCredit += l.Credit;
            rows.Add(new LedgerRowDto(l.EntryId, l.Entry.Number, l.Entry.EntryDate, l.Entry.Source, l.Entry.SourceNumber, l.Entry.Description, l.Description,
                l.Account.Code, l.Partner?.Title, l.Employee == null ? null : $"{l.Employee.FirstName} {l.Employee.LastName}", l.Debit, l.Credit, balance));
        }
        return new AccountLedgerDto(account, from, to, opening, rows, totalDebit, totalCredit, balance);
    }

    private sealed record AccountSum(decimal Debit, decimal Credit, int Lines);

    private async Task<Dictionary<Guid, AccountSum>> AccountSumsAsync()
    {
        var rows = await db.JournalLines.AsNoTracking().GroupBy(l => l.AccountId).Select(g => new
        {
            Id = g.Key,
            Debit = g.Sum(l => l.Entry.Status == JournalEntryStatus.POSTED ? l.Debit : 0m),
            Credit = g.Sum(l => l.Entry.Status == JournalEntryStatus.POSTED ? l.Credit : 0m),
            Lines = g.Count(),
        }).ToListAsync();
        return rows.ToDictionary(r => r.Id, r => new AccountSum(r.Debit, r.Credit, r.Lines));
    }

    private static LedgerAccountDto ToDto(LedgerAccount account, List<LedgerAccount> all, Dictionary<Guid, AccountSum> sums)
    {
        var own = all.Where(a => AccountingMath.Covers(account.Code, a.Code)).Select(a => sums.GetValueOrDefault(a.Id)).Where(s => s != null).ToList();
        var debit = own.Sum(s => s!.Debit);
        var credit = own.Sum(s => s!.Credit);
        return new LedgerAccountDto(account.Id, account.Code, account.Name, account.Type, account.ParentId, account.Code.Split('.').Length - 1,
            account.IsSystem, account.IsActive, debit, credit, debit - credit, own.Any(s => s!.Lines > 0), all.Count(a => a.ParentId == account.Id));
    }

    // ---------- Journal entries ----------

    private async Task<JournalEntry> WriteEntryAsync(Guid companyId, Guid userId, DateOnly date, JournalSource source, Guid? sourceId, string? sourceNumber,
        string description, List<DraftLine> draft, Guid? reversesId = null)
    {
        var lines = AccountingMath.NormalizeLines(draft);
        var (debit, credit) = AccountingMath.LineTotals(lines.Select(l => (l.Debit, l.Credit)));
        Guard.That(debit > 0 && debit == credit, $"Fiş dengesiz: borç {debit:0.00}, alacak {credit:0.00}");
        var codes = lines.Select(l => l.Code).Distinct().ToList();
        var ids = await db.LedgerAccounts.IgnoreQueryFilters().Where(a => a.CompanyId == companyId && codes.Contains(a.Code)).ToDictionaryAsync(a => a.Code, a => a.Id);
        var missing = codes.FirstOrDefault(c => !ids.ContainsKey(c));
        Guard.State(missing == null, $"{missing} hesabı hesap planında yok");
        var entry = new JournalEntry
        {
            CompanyId = companyId, Number = await numbering.NextAsync(JournalSequence, companyId), EntryDate = date, Status = JournalEntryStatus.POSTED,
            Source = source, SourceId = sourceId, SourceNumber = sourceNumber, Description = description, Total = debit, ReversesId = reversesId,
            PostedAt = DateTime.UtcNow, CreatedById = userId,
        };
        var no = 0;
        foreach (var l in lines)
        {
            entry.Lines.Add(new JournalLine { EntryId = entry.Id, LineNo = ++no, AccountId = ids[l.Code], PartnerId = l.PartnerId, EmployeeId = l.EmployeeId, Description = l.Description, Debit = l.Debit, Credit = l.Credit });
        }
        db.JournalEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    /// <summary>Posts the reversal (ters kayıt) of a posted entry on <paramref name="date"/>.</summary>
    private async Task<JournalEntry> ReverseEntryAsync(Guid companyId, Guid userId, Guid entryId, DateOnly date, string? note = null)
    {
        var original = await db.JournalEntries.IgnoreQueryFilters().Include(e => e.Lines).ThenInclude(l => l.Account)
            .FirstAsync(e => e.Id == entryId && e.CompanyId == companyId);
        var text = note?.Trim();
        var draft = original.Lines.Select(l => new DraftLine(l.Account.Code, l.Credit, l.Debit, l.PartnerId, l.EmployeeId, l.Description)).ToList();
        return await WriteEntryAsync(companyId, userId, date, original.Source, original.SourceId, original.SourceNumber,
            $"Ters kayıt: {original.Number} {original.Description}{(string.IsNullOrEmpty(text) ? "" : " — " + text)}", draft, original.Id);
    }

    public async Task<JournalEntryDetailDto> GetEntryAsync(Guid id)
    {
        var row = await db.JournalEntries.AsNoTracking().AsSplitQuery()
            .Include(e => e.CreatedBy).Include(e => e.Reverses).Include(e => e.Reversal)
            .Include(e => e.Lines).ThenInclude(l => l.Account)
            .Include(e => e.Lines).ThenInclude(l => l.Partner)
            .Include(e => e.Lines).ThenInclude(l => l.Employee)
            .FirstOrDefaultAsync(e => e.Id == id) ?? throw new NotFoundAppException("Yevmiye kaydı bulunamadı");
        var until = await LockedUntilAsync(row.CompanyId);
        var locked = IsLocked(row.EntryDate, until);
        var manual = row.Source == JournalSource.MANUAL;
        var draft = row.Status == JournalEntryStatus.DRAFT;
        var lines = row.Lines.OrderBy(l => l.LineNo).Select(l => new JournalLineDto(l.Id, l.LineNo, l.AccountId, l.Account.Code, l.Account.Name, l.PartnerId,
            l.Partner?.Title, l.EmployeeId, l.Employee == null ? null : $"{l.Employee.FirstName} {l.Employee.LastName}", l.Description, l.Debit, l.Credit)).ToList();
        return new JournalEntryDetailDto(row.Id, row.Number, row.EntryDate, row.Status, row.Source, row.SourceId, row.SourceNumber, row.Description, row.Total,
            row.ReversesId != null, row.Reversal != null, $"{row.CreatedBy.FirstName} {row.CreatedBy.LastName}", row.PostedAt, await DocumentIdAsync(row), lines,
            row.Reverses == null ? null : new EntryRef(row.Reverses.Id, row.Reverses.Number), row.Reversal == null ? null : new EntryRef(row.Reversal.Id, row.Reversal.Number),
            locked, draft && Ctx.Has("accounting:update"), draft && Ctx.Has("accounting:approve") && !locked && AccountingMath.IsBalanced(row.Lines.Select(l => (l.Debit, l.Credit))),
            !draft && manual && Ctx.Has("accounting:approve") && row.ReversesId == null && row.Reversal == null, draft && Ctx.Has("accounting:update"));
    }

    /// <summary>The document a user would open to see where an automatic entry came from.</summary>
    private async Task<Guid?> DocumentIdAsync(JournalEntry row)
    {
        if (row.SourceId is not { } sourceId) return null;
        switch (row.Source)
        {
            case JournalSource.MANUAL:
                return null;
            case JournalSource.TREASURY_OPENING or JournalSource.CHEQUE or JournalSource.EXPENSE_PAYMENT or JournalSource.PAYROLL_PAYMENT:
                var tx = await db.TreasuryTransactions.AsNoTracking().Where(t => t.Id == sourceId).Select(t => new { t.AccountId, t.DocumentId }).FirstOrDefaultAsync();
                if (tx == null) return null;
                return row.Source == JournalSource.TREASURY_OPENING ? tx.AccountId : tx.DocumentId;
            case JournalSource.PARTNER_OPENING:
                return await db.PartnerLedgerEntries.AsNoTracking().Where(e => e.Id == sourceId).Select(e => (Guid?)e.PartnerId).FirstOrDefaultAsync();
            case JournalSource.PRODUCTION:
                return await db.ProductionRecords.AsNoTracking().Where(r => r.Id == sourceId).Select(r => (Guid?)r.WorkOrderId).FirstOrDefaultAsync();
            default:
                return sourceId;
        }
    }

    /// <summary>Manual entries start as drafts (taslak fiş).</summary>
    public async Task<JournalEntryDetailDto> CreateEntryAsync(SaveJournalEntry dto)
    {
        var companyId = Ctx.RequireCompanyId();
        var userId = Ctx.RequireUserId();
        Guard.That(!string.IsNullOrWhiteSpace(dto.Description), "Açıklama gerekli");
        Guid id;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var lines = await ValidateLinesAsync(dto);
            var entry = new JournalEntry
            {
                CompanyId = companyId, Number = await numbering.NextAsync(JournalSequence, companyId), EntryDate = dto.EntryDate, Description = dto.Description.Trim(),
                Total = lines.Sum(l => l.Debit), CreatedById = userId,
            };
            foreach (var l in lines) { l.EntryId = entry.Id; entry.Lines.Add(l); }
            db.JournalEntries.Add(entry);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            id = entry.Id;
        }
        return await GetEntryAsync(id);
    }

    public async Task<JournalEntryDetailDto> UpdateEntryAsync(Guid id, SaveJournalEntry dto)
    {
        Guard.That(!string.IsNullOrWhiteSpace(dto.Description), "Açıklama gerekli");
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var entry = await LockDraftAsync(id);
            var lines = await ValidateLinesAsync(dto);
            db.JournalLines.RemoveRange(entry.Lines);
            entry.Lines.Clear();
            await db.SaveChangesAsync();
            foreach (var l in lines) { l.EntryId = id; entry.Lines.Add(l); }
            entry.EntryDate = dto.EntryDate;
            entry.Description = dto.Description.Trim();
            entry.Total = lines.Sum(l => l.Debit);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        return await GetEntryAsync(id);
    }

    public async Task RemoveEntryAsync(Guid id)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var entry = await LockDraftAsync(id);
        db.JournalEntries.Remove(entry);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>Borç = alacak, accounts active, date outside the locked period.</summary>
    public async Task<JournalEntryDetailDto> PostEntryAsync(Guid id)
    {
        var companyId = Ctx.RequireCompanyId();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await LockAccountingAsync(companyId);
            var entry = await LockDraftAsync(id);
            AssertOpenPeriod(entry.EntryDate, await LockedUntilAsync(companyId));
            var lines = await db.JournalLines.Include(l => l.Account).Where(l => l.EntryId == id).ToListAsync();
            var (debit, credit) = AccountingMath.LineTotals(lines.Select(l => (l.Debit, l.Credit)));
            Guard.That(AccountingMath.IsBalanced(lines.Select(l => (l.Debit, l.Credit))), $"Borç {debit:0.00} ile alacak {credit:0.00} eşit olmalı");
            var inactive = lines.FirstOrDefault(l => !l.Account.IsActive);
            Guard.That(inactive == null, $"{inactive?.Account.Code} hesabı pasif");
            entry.Status = JournalEntryStatus.POSTED;
            entry.PostedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        return await GetEntryAsync(id);
    }

    /// <summary>Ters kayıt of a posted manual entry; automatic entries follow their source document.</summary>
    public async Task<JournalEntryDetailDto> ReverseAsync(Guid id, ReverseJournalEntry dto)
    {
        var companyId = Ctx.RequireCompanyId();
        Guid reversalId;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await LockAccountingAsync(companyId);
            await db.Database.ExecuteSqlAsync($"SELECT id FROM journal_entries WHERE id = {id} FOR UPDATE");
            var entry = await db.JournalEntries.Include(e => e.Reversal).FirstOrDefaultAsync(e => e.Id == id) ?? throw new NotFoundAppException("Yevmiye kaydı bulunamadı");
            Guard.State(entry.Status == JournalEntryStatus.POSTED, "Yalnızca kesinleşmiş fişler ters kaydedilir");
            Guard.State(entry.Source == JournalSource.MANUAL, "Otomatik fişi ters kaydetmek için kaynak belgeyi iptal edin");
            Guard.State(entry.ReversesId == null && entry.Reversal == null, "Fiş zaten ters kaydedilmiş");
            Guard.That(dto.Date >= entry.EntryDate, "Ters kayıt fişten önceki bir tarihe atılamaz");
            AssertOpenPeriod(dto.Date, await LockedUntilAsync(companyId));
            reversalId = (await ReverseEntryAsync(companyId, Ctx.RequireUserId(), id, dto.Date, dto.Note)).Id;
            await tx.CommitAsync();
        }
        return await GetEntryAsync(reversalId);
    }

    private async Task<JournalEntry> LockDraftAsync(Guid id)
    {
        await db.Database.ExecuteSqlAsync($"SELECT id FROM journal_entries WHERE id = {id} FOR UPDATE");
        var entry = await db.JournalEntries.Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == id) ?? throw new NotFoundAppException("Yevmiye kaydı bulunamadı");
        Guard.State(entry.Status == JournalEntryStatus.DRAFT && entry.Source == JournalSource.MANUAL, "Yalnızca taslak fişler değiştirilebilir");
        return entry;
    }

    private async Task<List<JournalLine>> ValidateLinesAsync(SaveJournalEntry dto)
    {
        Guard.That(dto.Lines is { Count: > 0 }, "En az bir satır gerekli");
        for (var i = 0; i < dto.Lines.Count; i++)
        {
            var line = dto.Lines[i];
            Guard.That(line.Debit >= 0 && line.Credit >= 0 && (line.Debit > 0) != (line.Credit > 0), $"Satır {i + 1}: borç veya alacaktan yalnız biri girilmeli");
        }
        var accountIds = dto.Lines.Select(l => l.AccountId).Distinct().ToList();
        var partnerIds = dto.Lines.Where(l => l.PartnerId != null).Select(l => l.PartnerId!.Value).Distinct().ToList();
        var employeeIds = dto.Lines.Where(l => l.EmployeeId != null).Select(l => l.EmployeeId!.Value).Distinct().ToList();
        Guard.That(await db.LedgerAccounts.CountAsync(a => accountIds.Contains(a.Id) && a.IsActive) == accountIds.Count, "Bu şirketin aktif hesaplarını seçin");
        Guard.That(await db.BusinessPartners.CountAsync(p => partnerIds.Contains(p.Id)) == partnerIds.Count, "Cari bulunamadı");
        Guard.That(await db.Employees.CountAsync(e => employeeIds.Contains(e.Id)) == employeeIds.Count, "Personel bulunamadı");
        return dto.Lines.Select((l, i) => new JournalLine
        {
            LineNo = i + 1, AccountId = l.AccountId, PartnerId = l.PartnerId, EmployeeId = l.EmployeeId,
            Description = string.IsNullOrWhiteSpace(l.Description) ? null : l.Description.Trim(), Debit = l.Debit, Credit = l.Credit,
        }).ToList();
    }
}
