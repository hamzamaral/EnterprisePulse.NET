using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Finance;
using EnterprisePulse.Core.Modules.Trade;
using EnterprisePulse.Core.Seeding;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Sales;

/// <summary>
/// One accepted quote that became a partly delivered, partly collected order, plus an open quote,
/// a portfolio cheque and opening cash balances. Stock movements are only the delivery itself.
/// </summary>
public sealed class SalesSeed(
    AppDbContext db, TreasuryService treasury, SalesQuoteService quotes, SalesOrderService orders, DeliveryNoteService deliveries,
    SalesInvoiceService invoices, PaymentService payments) : ISeedStep
{
    public int Order => 40;

    public async Task RunAsync(SeedContext ctx)
    {
        using var _ = db.User.ActAs(ctx.PulseId, ctx.AdminId, superAdmin: true);
        await AccountAsync("KASA-MRK", "Merkez Kasa", TreasuryAccountType.CASH, "TRY", null, 25000);
        await AccountAsync("BANK-TRY", "Merkez Vadesiz TL", TreasuryAccountType.BANK, "TRY", "Pulse Bank", 180000);
        await AccountAsync("BANK-EUR", "Merkez Vadesiz EUR", TreasuryAccountType.BANK, "EUR", "Pulse Bank", 4000);
        if (await db.SalesQuotes.AnyAsync()) return;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var variants = await db.ProductVariants.Where(v => v.Sku == "PRD-MONITOR" || v.Sku == "PRD-MOUSE-SYH" || v.Sku == "SRV-KURULUM")
            .ToDictionaryAsync(v => v.Sku, v => v.Id);
        var partners = await db.BusinessPartners.Where(p => p.Code == "MUS-001" || p.Code == "MUS-002").ToDictionaryAsync(p => p.Code, p => p.Id);
        var warehouse = await db.Warehouses.Where(w => w.Code == "MRK").Select(w => w.Id).FirstAsync();
        if (variants.Count < 3 || partners.Count < 2) return;

        var accepted = await quotes.CreateAsync(new CreateSalesQuote(partners["MUS-001"], today.AddDays(-12), today.AddDays(20), "TRY", null,
            "Mağaza açılışı için monitör, mouse ve kurulum.",
            [
                Line(variants["PRD-MONITOR"], 4, 8499),
                Line(variants["PRD-MOUSE-SYH"], 10, 549),
                Line(variants["SRV-KURULUM"], 2, 1500),
            ]));
        var orderId = await quotes.AcceptAsync(accepted.Id, warehouse, today.AddDays(-5));
        await orders.ApproveAsync(orderId);
        var order = await orders.GetAsync(orderId);
        var monitor = order.Lines.First(l => l.Sku == "PRD-MONITOR");
        var mouse = order.Lines.First(l => l.Sku == "PRD-MOUSE-SYH");
        var service = order.Lines.First(l => l.Sku == "SRV-KURULUM");
        await deliveries.CreateAsync(new CreateDelivery(orderId, today.AddDays(-8), "Boğaziçi Perakende, Levent", "Pulse Lojistik", null,
            [new DeliveryLineInput(monitor.Id, 2), new DeliveryLineInput(mouse.Id, 10)]));
        var invoice = await invoices.CreateAsync(new CreateSalesInvoice(partners["MUS-001"], orderId, today.AddDays(-7), today.AddDays(-3), "TRY", null, null,
            [
                new TradeLineInput(monitor.VariantId, monitor.Description, 2, monitor.UnitPrice, monitor.DiscountRate, monitor.VatRate, OrderLineId: monitor.Id),
                new TradeLineInput(mouse.VariantId, mouse.Description, 10, mouse.UnitPrice, mouse.DiscountRate, mouse.VatRate, OrderLineId: mouse.Id),
                new TradeLineInput(service.VariantId, service.Description, 2, service.UnitPrice, service.DiscountRate, service.VatRate, OrderLineId: service.Id),
            ]));
        var cash = await db.TreasuryAccounts.Where(a => a.Code == "KASA-MRK").Select(a => a.Id).FirstAsync();
        await payments.CreateAsync(new CreatePaymentRequest(PaymentDirection.IN, PaymentMethod.CASH, partners["MUS-001"], today.AddDays(-2), cash, 12000,
            "TRY", null, "Açılış siparişi kısmi tahsilat", null, null, null, null, null, null,
            [new AllocationInput(invoice.Id, null, 12000)]));
        await payments.CreateAsync(new CreatePaymentRequest(PaymentDirection.IN, PaymentMethod.CHEQUE, partners["MUS-001"], today.AddDays(-1), null, 8000,
            "TRY", null, "Kalan için müşteri çeki", "CEK-88421", "Pulse Bank", "Levent", "Boğaziçi Perakende A.Ş.", today.AddDays(20), null, null));

        await quotes.CreateAsync(new CreateSalesQuote(partners["MUS-002"], today.AddDays(-2), today.AddDays(15), "TRY", null, "A4 kâğıt teklifi, henüz yanıt yok.",
            [Line((await db.ProductVariants.Where(v => v.Sku == "PRD-A4").Select(v => v.Id).FirstAsync()), 20, 590)]));
    }

    private async Task AccountAsync(string code, string name, TreasuryAccountType type, string currency, string? bank, decimal opening)
    {
        var account = await db.TreasuryAccounts.FirstOrDefaultAsync(a => a.Code == code);
        if (account == null)
        {
            account = new TreasuryAccount { Code = code, Name = name, Type = type, CurrencyCode = currency, BankName = bank };
            db.TreasuryAccounts.Add(account);
            await db.SaveChangesAsync();
        }
        if (opening > 0 && !await db.TreasuryTransactions.AnyAsync(t => t.AccountId == account.Id))
        {
            await treasury.PostAsync(account.Id, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-90), TreasuryDocumentType.OPENING, account.Id, "ACILIS",
                "Açılış bakiyesi", opening, 0);
        }
    }

    private static TradeLineInput Line(Guid variantId, decimal qty, decimal price) => new(variantId, null, qty, price, 0, 20);
}
