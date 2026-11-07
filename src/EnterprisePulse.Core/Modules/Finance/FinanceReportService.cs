using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Trade;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Finance;

public sealed record AgingLine(Guid Id, string Number, string Partner, DateOnly DueDate, int Days, decimal OpenAmount, decimal OpenBase, string Bucket);

public sealed record AgingBucket(string Key, string Label, int Count, decimal Amount);

public sealed record AgingReport(string Kind, decimal TotalBase, List<AgingBucket> Buckets, List<AgingLine> Lines);

public sealed record DocumentReportLine(Guid Id, string Number, string Partner, DateOnly Date, string CurrencyCode, decimal GrandTotal, decimal GrandTotalBase, PaymentStatus PaymentStatus);

public sealed record DocumentReport(DateOnly From, DateOnly To, int Count, decimal GrandTotalBase, List<DocumentReportLine> Lines);

public sealed record CashFlowAccount(Guid Id, string Code, string Name, string CurrencyCode, decimal AmountIn, decimal AmountOut, decimal Net);

public sealed record CashFlowReport(DateOnly From, DateOnly To, List<CashFlowAccount> Accounts);

/// <summary>Aging, sales, purchasing and cash-flow reports over posted documents.</summary>
[ModuleService]
public sealed class FinanceReportService(AppDbContext db, TreasuryService treasury)
{
    public async Task<AgingReport> AgingAsync(string kind)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var sales = !string.Equals(kind, "purchase", StringComparison.OrdinalIgnoreCase);
        List<AgingLine> lines;
        if (sales)
        {
            var rows = await db.SalesInvoices.AsNoTracking().Include(i => i.Partner)
                .Where(i => i.Status == InvoiceStatus.ISSUED && i.PaymentStatus != PaymentStatus.PAID).ToListAsync();
            lines = rows.Select(i => Line(i.Id, i.Number, i.Partner.Title, i.DueDate, today, i.GrandTotal - i.PaidAmount, i.ExchangeRate)).ToList();
        }
        else
        {
            var rows = await db.PurchaseInvoices.AsNoTracking().Include(i => i.Partner)
                .Where(i => i.Status == InvoiceStatus.ISSUED && i.PaymentStatus != PaymentStatus.PAID).ToListAsync();
            lines = rows.Select(i => Line(i.Id, i.Number, i.Partner.Title, i.DueDate, today, i.GrandTotal - i.PaidAmount, i.ExchangeRate)).ToList();
        }
        var buckets = new (string Key, string Label, Func<int, bool> Match)[]
        {
            ("current", "Vadesi gelmemiş", d => d <= 0),
            ("1-30", "1-30 gün", d => d is >= 1 and <= 30),
            ("31-60", "31-60 gün", d => d is >= 31 and <= 60),
            ("61-90", "61-90 gün", d => d is >= 61 and <= 90),
            ("90+", "90 gün üzeri", d => d > 90),
        };
        return new AgingReport(sales ? "sales" : "purchase", lines.Sum(l => l.OpenBase),
            buckets.Select(b => new AgingBucket(b.Key, b.Label, lines.Count(l => l.Bucket == b.Key), lines.Where(l => l.Bucket == b.Key).Sum(l => l.OpenBase))).ToList(),
            lines.OrderByDescending(l => l.Days).ToList());
    }

    public async Task<DocumentReport> SalesAsync(DateOnly from, DateOnly to)
    {
        var rows = await db.SalesInvoices.AsNoTracking().Include(i => i.Partner)
            .Where(i => i.Status == InvoiceStatus.ISSUED && i.InvoiceDate >= from && i.InvoiceDate <= to)
            .OrderByDescending(i => i.InvoiceDate).ToListAsync();
        return Report(from, to, rows.Select(i => new DocumentReportLine(i.Id, i.Number, i.Partner.Title, i.InvoiceDate, i.CurrencyCode, i.GrandTotal,
            DocumentMath.ToBase(i.GrandTotal, i.ExchangeRate), i.PaymentStatus)));
    }

    public async Task<DocumentReport> PurchasesAsync(DateOnly from, DateOnly to)
    {
        var rows = await db.PurchaseInvoices.AsNoTracking().Include(i => i.Partner)
            .Where(i => i.Status == InvoiceStatus.ISSUED && i.InvoiceDate >= from && i.InvoiceDate <= to)
            .OrderByDescending(i => i.InvoiceDate).ToListAsync();
        return Report(from, to, rows.Select(i => new DocumentReportLine(i.Id, i.Number, i.Partner.Title, i.InvoiceDate, i.CurrencyCode, i.GrandTotal,
            DocumentMath.ToBase(i.GrandTotal, i.ExchangeRate), i.PaymentStatus)));
    }

    public async Task<CashFlowReport> CashFlowAsync(DateOnly from, DateOnly to)
    {
        var accounts = await treasury.AccountsAsync();
        var txs = await db.TreasuryTransactions.AsNoTracking().Where(t => t.TxDate >= from && t.TxDate <= to).ToListAsync();
        var lines = accounts.Select(a =>
        {
            var mine = txs.Where(t => t.AccountId == a.Id).ToList();
            var inn = mine.Sum(t => t.AmountIn);
            var outt = mine.Sum(t => t.AmountOut);
            return new CashFlowAccount(a.Id, a.Code, a.Name, a.CurrencyCode, inn, outt, inn - outt);
        }).ToList();
        return new CashFlowReport(from, to, lines);
    }

    private static DocumentReport Report(DateOnly from, DateOnly to, IEnumerable<DocumentReportLine> lines)
    {
        var list = lines.ToList();
        return new DocumentReport(from, to, list.Count, list.Sum(l => l.GrandTotalBase), list);
    }

    private static AgingLine Line(Guid id, string number, string partner, DateOnly due, DateOnly today, decimal open, decimal rate)
    {
        var days = today.DayNumber - due.DayNumber;
        var bucket = days <= 0 ? "current" : days <= 30 ? "1-30" : days <= 60 ? "31-60" : days <= 90 ? "61-90" : "90+";
        return new AgingLine(id, number, partner, due, Math.Max(days, 0), DocumentMath.Round2(open), DocumentMath.ToBase(open, rate), bucket);
    }
}
