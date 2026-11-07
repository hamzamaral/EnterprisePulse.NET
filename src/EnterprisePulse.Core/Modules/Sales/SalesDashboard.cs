using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Finance;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Trade;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Sales;

public sealed class SalesDashboard(AppDbContext db, TreasuryService treasury) : IDashboardProvider
{
    public int Order => 40;

    public async Task<DashboardSection?> SectionAsync()
    {
        var ctx = db.User;
        var cards = new List<DashboardCard>();
        if (ctx.Has("sales:read"))
        {
            var open = await db.SalesOrders.CountAsync(o => o.Status == SalesOrderStatus.APPROVED || o.Status == SalesOrderStatus.PARTIALLY_DELIVERED);
            cards.Add(new("open-sales-orders", "Açık satış siparişi", open.ToString(), "PointOfSale", Link: "/r/sales-orders"));
        }
        if (ctx.Has("finance:read") || ctx.Has("sales:read"))
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var overdue = await db.SalesInvoices.Where(i => i.Status == InvoiceStatus.ISSUED && i.PaymentStatus != PaymentStatus.PAID && i.DueDate < today).ToListAsync();
            var amount = overdue.Sum(i => DocumentMath.ToBase(i.GrandTotal - i.PaidAmount, i.ExchangeRate));
            if (overdue.Count > 0)
                cards.Add(new("overdue-receivables", "Vadesi geçen alacak", amount.ToString("N2"), "AccountBalanceWallet", $"{overdue.Count} fatura", "/finance/aging", "warning"));
        }
        if (ctx.Has("finance:read"))
        {
            var accounts = await treasury.AccountsAsync();
            var cash = accounts.Where(a => a.CurrencyCode == "TRY" && a.IsActive).Sum(a => a.Balance);
            cards.Add(new("cash-balance", "Kasa ve banka (TRY)", cash.ToString("N2"), "AccountBalance", Link: "/r/treasury-accounts"));
        }
        return cards.Count == 0 ? null : new DashboardSection("sales", "Satış ve Finans", cards);
    }
}
