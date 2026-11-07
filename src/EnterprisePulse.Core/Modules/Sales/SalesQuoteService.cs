using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Partners;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Trade;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Sales;

public sealed record CreateSalesQuote(
    Guid PartnerId, DateOnly QuoteDate, DateOnly? ValidUntil, string? CurrencyCode, decimal? ExchangeRate, string? Notes, List<TradeLineInput> Lines);

public sealed record SalesQuoteDetail(
    Guid Id, string Number, Guid PartnerId, string PartnerCode, string PartnerTitle, DateOnly QuoteDate, DateOnly? ValidUntil,
    string CurrencyCode, decimal ExchangeRate, SalesQuoteStatus Status, string? Notes, Guid? OrderId, string? OrderNumber,
    TotalsDto Totals, List<TradeLineDto> Lines);

/// <summary>Satış teklifleri (STK). Accepting one opens a draft sales order.</summary>
[ModuleService]
public sealed class SalesQuoteService(AppDbContext db, NumberingService numbering, TradeService trade, PartnerLedgerService partners, SalesOrderService orders)
{
    public async Task<SalesQuoteDetail> CreateAsync(CreateSalesQuote dto)
    {
        var partner = await partners.ForDocumentAsync(dto.PartnerId, PartnerSides.Customers);
        var date = dto.QuoteDate == default ? DateOnly.FromDateTime(DateTime.UtcNow) : dto.QuoteDate;
        var currency = await trade.ResolveCurrencyAsync(dto.CurrencyCode ?? partner.CurrencyCode, dto.ExchangeRate, date);
        var variants = await trade.LoadVariantsAsync(dto.Lines.Select(l => l.VariantId));
        var priced = TradeService.PriceLines(dto.Lines, variants);
        var quote = new SalesQuote
        {
            Number = await numbering.NextAsync("SALES_QUOTE"),
            PartnerId = partner.Id, QuoteDate = date, ValidUntil = dto.ValidUntil, CurrencyCode = currency.CurrencyCode, ExchangeRate = currency.ExchangeRate,
            Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim(),
            Subtotal = priced.Totals.Subtotal, DiscountTotal = priced.Totals.DiscountTotal, VatTotal = priced.Totals.VatTotal, GrandTotal = priced.Totals.GrandTotal,
            CreatedById = db.User.RequireUserId(),
        };
        foreach (var line in priced.Lines)
        {
            quote.Lines.Add(new SalesQuoteLine
            {
                LineNo = line.LineNo, VariantId = line.VariantId, Description = line.Description, Quantity = line.Quantity, UnitPrice = line.UnitPrice,
                DiscountRate = line.DiscountRate, VatRate = line.VatRate, LineTotal = line.LineTotal,
            });
        }
        db.SalesQuotes.Add(quote);
        await db.SaveChangesAsync();
        return await GetAsync(quote.Id);
    }

    public async Task<SalesQuoteDetail> RejectAsync(Guid id)
    {
        var quote = await OpenAsync(id);
        quote.Status = SalesQuoteStatus.REJECTED;
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<Guid> AcceptAsync(Guid id, Guid warehouseId, DateOnly? deliveryDate)
    {
        var quote = await OpenAsync(id);
        var lines = await db.SalesQuoteLines.Where(l => l.QuoteId == id).OrderBy(l => l.LineNo).ToListAsync();
        Guard.That(lines.All(l => l.VariantId != null), "Sipariş için her satırda ürün olmalı");
        var inputs = lines.Select(l => new TradeLineInput(l.VariantId, l.Description, l.Quantity, l.UnitPrice, l.DiscountRate, l.VatRate)).ToList();
        var order = await orders.CreateEntityAsync(new SaveSalesOrder(quote.PartnerId, warehouseId, DateOnly.FromDateTime(DateTime.UtcNow), deliveryDate, null,
            quote.CurrencyCode, quote.ExchangeRate, quote.Notes, inputs), quote.Id);
        quote.Status = SalesQuoteStatus.ACCEPTED;
        await db.SaveChangesAsync();
        return order.Id;
    }

    public async Task<SalesQuoteDetail> GetAsync(Guid id)
    {
        var q = Guard.Found(await db.SalesQuotes.AsNoTracking().Include(x => x.Partner).Include(x => x.Lines).ThenInclude(l => l.Variant).ThenInclude(v => v!.Product).ThenInclude(p => p.Unit)
            .Include(x => x.Order).FirstOrDefaultAsync(x => x.Id == id), "Satış teklifi bulunamadı");
        return new SalesQuoteDetail(q.Id, q.Number, q.PartnerId, q.Partner.Code, q.Partner.Title, q.QuoteDate, q.ValidUntil, q.CurrencyCode, q.ExchangeRate, q.Status, q.Notes,
            q.Order?.Id, q.Order?.Number, TradeService.Totals(q.Subtotal, q.DiscountTotal, q.VatTotal, q.GrandTotal, q.ExchangeRate),
            q.Lines.OrderBy(l => l.LineNo).Select(l => TradeService.LineDto(l.Id, l.LineNo, l.Variant, l.Description, l.Quantity, l.UnitPrice, l.DiscountRate, l.VatRate, l.LineTotal)).ToList());
    }

    private async Task<SalesQuote> OpenAsync(Guid id)
    {
        var quote = Guard.Found(await db.SalesQuotes.FirstOrDefaultAsync(q => q.Id == id), "Satış teklifi bulunamadı");
        Guard.State(quote.Status == SalesQuoteStatus.OPEN, "Teklif artık açık değil");
        return quote;
    }
}
