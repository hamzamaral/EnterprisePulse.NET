using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Assets;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Seeding;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Accounting;

public sealed class LedgerAccountsHook() : ManagedRecordHook("ledger-accounts", "Hesap planı Muhasebe > Hesap Planı ekranından yönetilir");

public sealed class JournalEntriesHook() : ManagedRecordHook("journal-entries", "Yevmiye fişleri Muhasebe > Yevmiye ekranından kaydedilir ve kesinleştirilir");

public sealed class JournalLinesHook() : ManagedRecordHook("journal-lines", "Fiş satırları fişle birlikte kaydedilir");

public sealed class AccountingDashboard(AppDbContext db, AccountingService accounting) : IDashboardProvider
{
    public int Order => 60;

    public async Task<DashboardSection?> SectionAsync()
    {
        if (!db.User.Has("accounting:read")) return null;
        var summary = await accounting.SummaryAsync(null);
        var pending = summary.PendingCount;
        return new DashboardSection("accounting", "Muhasebe",
        [
            new("accounting-cash", "Kasa ve banka", $"{summary.Cash:N0} {summary.CurrencyCode}", "AccountBalanceWallet", Link: "/accounting"),
            new("accounting-profit", $"{summary.Year} net kâr", $"{summary.NetProfit:N0} {summary.CurrencyCode}", "TrendingUp", Link: "/accounting/reports",
                Color: summary.NetProfit < 0 ? "error" : "success"),
            new("accounting-pending", "Muhasebeleşmeyi bekleyen", pending.ToString(), "PendingActions", Link: "/accounting", Color: pending > 0 ? "warning" : "primary"),
        ]);
    }
}

/// <summary>Standard chart for every company and a few manual entries for the demo company; source documents post via "Muhasebeleştir" or the nightly job.</summary>
public sealed class AccountingSeed(AppDbContext db, AccountingService accounting, NumberingService numbering) : ISeedStep
{
    public int Order => 80;

    public async Task RunAsync(SeedContext ctx)
    {
        foreach (var companyId in new[] { ctx.PulseId, ctx.LogisticsId }) await accounting.InstallChartAsync(companyId);

        using var _ = db.User.ActAs(ctx.PulseId, ctx.AdminId, superAdmin: true);
        if (await db.JournalEntries.AnyAsync()) return;

        var today = AccountingClock.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var previousMonthEnd = monthStart.AddDays(-1);
        var rentDate = monthStart.AddDays(4) < today ? monthStart.AddDays(4) : today;

        var entries = new (DateOnly Date, string Description, JournalEntryStatus Status, (string Code, decimal Debit, decimal Credit, string? Text)[] Lines)[]
        {
            (previousMonthEnd, "Banka hesap işletim ücreti ve EFT masrafları", JournalEntryStatus.POSTED, [("653", 1_250, 0, "Hesap işletim ücreti ve EFT"), ("102", 0, 1_250, null)]),
            (previousMonthEnd, "Vadeli mevduat faiz geliri", JournalEntryStatus.POSTED, [("102", 3_400, 0, null), ("642", 0, 3_400, "Aylık vadeli mevduat faizi")]),
            (rentDate, "Merkez ofis kirası (%20 stopaj)", JournalEntryStatus.POSTED, [("770", 60_000, 0, "Brüt kira"), ("360", 0, 12_000, "Kira stopajı"), ("102", 0, 48_000, "Net kira ödemesi")]),
            (previousMonthEnd, "Demirbaş amortismanı", JournalEntryStatus.DRAFT, [("770", 8_500, 0, "Aylık amortisman payı"), ("257", 0, 8_500, null)]),
        };
        var ids = await db.LedgerAccounts.ToDictionaryAsync(a => a.Code, a => a.Id);
        foreach (var e in entries)
        {
            var entry = new JournalEntry
            {
                CompanyId = ctx.PulseId, Number = await numbering.NextAsync(AccountingService.JournalSequence, ctx.PulseId), EntryDate = e.Date, Status = e.Status,
                Description = e.Description, Total = e.Lines.Sum(l => l.Debit), PostedAt = e.Status == JournalEntryStatus.POSTED ? DateTime.UtcNow : null, CreatedById = ctx.AdminId,
            };
            var no = 0;
            foreach (var l in e.Lines)
            {
                entry.Lines.Add(new JournalLine { EntryId = entry.Id, LineNo = ++no, AccountId = ids[l.Code], Description = l.Text, Debit = l.Debit, Credit = l.Credit });
            }
            db.JournalEntries.Add(entry);
        }
        await db.SaveChangesAsync();
    }
}
