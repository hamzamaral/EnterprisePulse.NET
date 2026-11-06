using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Partners;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Trade;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Purchasing;

public sealed record CreateSupplierQuote(
    Guid PartnerId, Guid? RequestId, DateOnly QuoteDate, DateOnly? ValidUntil, int? DeliveryDays, string? CurrencyCode, decimal? ExchangeRate,
    string? Notes, List<TradeLineInput> Lines);

public sealed record AcceptQuote(Guid WarehouseId, DateOnly? ExpectedDate);

public sealed record QuoteDetailDto(
    Guid Id, string Number, Guid? RequestId, string? RequestNumber, Guid PartnerId, string PartnerCode, string PartnerTitle, DateOnly QuoteDate,
    DateOnly? ValidUntil, int? DeliveryDays, string CurrencyCode, decimal ExchangeRate, SupplierQuoteStatus Status, string? Notes,
    Guid? OrderId, string? OrderNumber, TotalsDto Totals, List<TradeLineDto> Lines);

public sealed record QuotePriceDto(Guid RequestLineId, decimal Quantity, decimal UnitPrice, decimal NetUnitPriceBase, decimal LineTotal);

public sealed record ComparedQuoteDto(
    Guid Id, string Number, Guid PartnerId, string PartnerTitle, DateOnly QuoteDate, DateOnly? ValidUntil, int? DeliveryDays, string CurrencyCode,
    decimal ExchangeRate, SupplierQuoteStatus Status, decimal GrandTotal, decimal GrandTotalBase, bool Complete, Guid? OrderId, List<QuotePriceDto> Prices);

public sealed record QuoteComparisonDto(Guid RequestId, string RequestNumber, string Title, PurchaseRequestStatus Status, string BaseCurrency,
    List<RequestLineDto> Lines, List<ComparedQuoteDto> Quotes, Guid? BestQuoteId);

/// <summary>Supplier offers (TKL), optionally answering an approved request; compared side by side.</summary>
[ModuleService]
public sealed class SupplierQuoteService(AppDbContext db, NumberingService numbering, TradeService trade, PartnerLedgerService partners)
{
    public async Task<QuoteDetailDto> CreateAsync(CreateSupplierQuote dto)
    {
        var partner = await partners.ForDocumentAsync(dto.PartnerId, PartnerSides.Suppliers);
        if (dto.RequestId is { } requestId)
        {
            var request = await db.PurchaseRequests.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == requestId);
            Guard.That(request != null, "Satın alma talebi bulunamadı");
            Guard.State(request!.Status == PurchaseRequestStatus.APPROVED, "Teklif yalnızca onaylanmış taleplere eklenebilir");
            var ids = request.Lines.Select(l => l.Id).ToHashSet();
            Guard.That(dto.Lines.All(l => l.RequestLineId == null || ids.Contains(l.RequestLineId.Value)), "Teklif satırı başka bir talebe ait");
        }
        else
        {
            Guard.That(dto.Lines.All(l => l.RequestLineId == null), "Talep satırı için talep seçilmeli");
        }
        Guard.That(dto.ValidUntil == null || dto.ValidUntil >= dto.QuoteDate, "Geçerlilik tarihi teklif tarihinden önce olamaz");
        Guard.That(dto.DeliveryDays is null or >= 0, "Teslim süresi negatif olamaz");
        var currency = await trade.ResolveCurrencyAsync(dto.CurrencyCode ?? partner.CurrencyCode, dto.ExchangeRate, dto.QuoteDate);
        var variants = await trade.LoadVariantsAsync(dto.Lines.Select(l => l.VariantId));
        var priced = TradeService.PriceLines(dto.Lines, variants);

        await using var tx = await db.Database.BeginTransactionAsync();
        var quote = new SupplierQuote
        {
            Number = await numbering.NextAsync("SUPPLIER_QUOTE"),
            RequestId = dto.RequestId, PartnerId = partner.Id, QuoteDate = dto.QuoteDate, ValidUntil = dto.ValidUntil, DeliveryDays = dto.DeliveryDays,
            CurrencyCode = currency.CurrencyCode, ExchangeRate = currency.ExchangeRate, Notes = Clean(dto.Notes),
            Subtotal = priced.Totals.Subtotal, DiscountTotal = priced.Totals.DiscountTotal, VatTotal = priced.Totals.VatTotal, GrandTotal = priced.Totals.GrandTotal,
            CreatedById = db.User.RequireUserId(),
        };
        foreach (var l in priced.Lines)
        {
            quote.Lines.Add(new SupplierQuoteLine
            {
                LineNo = l.LineNo, RequestLineId = l.Input.RequestLineId, VariantId = l.VariantId, Description = l.Description, Quantity = l.Quantity,
                UnitPrice = l.UnitPrice, DiscountRate = l.DiscountRate, VatRate = l.VatRate, LineTotal = l.LineTotal,
            });
        }
        db.SupplierQuotes.Add(quote);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(quote.Id);
    }

    public async Task<QuoteDetailDto> RejectAsync(Guid id)
    {
        var quote = Guard.Found(await db.SupplierQuotes.FirstOrDefaultAsync(q => q.Id == id), "Tedarikçi teklifi bulunamadı");
        Guard.State(quote.Status == SupplierQuoteStatus.OPEN, "Yalnızca açık teklifler reddedilebilir");
        quote.Status = SupplierQuoteStatus.REJECTED;
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    /// <summary>Turns an open quote into a DRAFT order; the request's other open quotes are rejected and it becomes ORDERED.</summary>
    public async Task<Guid> AcceptAsync(Guid id, AcceptQuote dto)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlAsync($"SELECT id FROM supplier_quotes WHERE id = {id} FOR UPDATE");
        var quote = Guard.Found(await db.SupplierQuotes.Include(q => q.Lines).FirstOrDefaultAsync(q => q.Id == id), "Tedarikçi teklifi bulunamadı");
        Guard.State(quote.Status == SupplierQuoteStatus.OPEN, "Yalnızca açık teklifler kabul edilebilir");
        if (quote.Lines.OrderBy(l => l.LineNo).FirstOrDefault(l => l.VariantId == null) is { } missing)
        {
            throw new BadRequestAppException($"Satır {missing.LineNo} için ürün seçilmemiş; siparişte her satırda ürün olmalı");
        }
        var warehouse = await db.Warehouses.FirstOrDefaultAsync(w => w.Id == dto.WarehouseId && w.IsActive);
        Guard.That(warehouse != null, "Depo bulunamadı");

        var order = new PurchaseOrder
        {
            Number = await numbering.NextAsync("PURCHASE_ORDER"),
            PartnerId = quote.PartnerId, RequestId = quote.RequestId, QuoteId = quote.Id, OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            ExpectedDate = dto.ExpectedDate, WarehouseId = warehouse!.Id, CurrencyCode = quote.CurrencyCode, ExchangeRate = quote.ExchangeRate,
            Status = PurchaseOrderStatus.DRAFT, Notes = quote.Notes, Subtotal = quote.Subtotal, DiscountTotal = quote.DiscountTotal,
            VatTotal = quote.VatTotal, GrandTotal = quote.GrandTotal, CreatedById = db.User.RequireUserId(),
        };
        foreach (var l in quote.Lines.OrderBy(l => l.LineNo))
        {
            order.Lines.Add(new PurchaseOrderLine
            {
                LineNo = l.LineNo, RequestLineId = l.RequestLineId, VariantId = l.VariantId!.Value, Description = l.Description, Quantity = l.Quantity,
                UnitPrice = l.UnitPrice, DiscountRate = l.DiscountRate, VatRate = l.VatRate, LineTotal = l.LineTotal,
            });
        }
        db.PurchaseOrders.Add(order);
        quote.Status = SupplierQuoteStatus.ACCEPTED;
        await db.SaveChangesAsync();
        if (quote.RequestId is { } requestId)
        {
            await db.SupplierQuotes.Where(q => q.RequestId == requestId && q.Status == SupplierQuoteStatus.OPEN)
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, SupplierQuoteStatus.REJECTED));
            await db.PurchaseRequests.Where(r => r.Id == requestId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, PurchaseRequestStatus.ORDERED));
        }
        await tx.CommitAsync();
        return order.Id;
    }

    public async Task<QuoteDetailDto> GetAsync(Guid id)
    {
        var q = Guard.Found(await db.SupplierQuotes.AsNoTracking().Include(x => x.Partner).Include(x => x.Request).Include(x => x.Order)
            .Include(x => x.Lines).ThenInclude(l => l.Variant).ThenInclude(v => v!.Product).ThenInclude(p => p.Unit)
            .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id), "Tedarikçi teklifi bulunamadı");
        return new QuoteDetailDto(q.Id, q.Number, q.RequestId, q.Request?.Number, q.PartnerId, q.Partner.Code, q.Partner.Title, q.QuoteDate, q.ValidUntil,
            q.DeliveryDays, q.CurrencyCode, q.ExchangeRate, q.Status, q.Notes, q.Order?.Id, q.Order?.Number,
            TradeService.Totals(q.Subtotal, q.DiscountTotal, q.VatTotal, q.GrandTotal, q.ExchangeRate),
            q.Lines.OrderBy(l => l.LineNo).Select(l => TradeService.LineDto(l.Id, l.LineNo, l.Variant, l.Description, l.Quantity, l.UnitPrice, l.DiscountRate,
                l.VatRate, l.LineTotal)).ToList());
    }

    /// <summary>Every quote's price per request line; the best quote is the cheapest open one that covers all lines.</summary>
    public async Task<QuoteComparisonDto> CompareAsync(Guid requestId)
    {
        var r = Guard.Found(await db.PurchaseRequests.AsNoTracking()
            .Include(x => x.Lines).ThenInclude(l => l.Variant).ThenInclude(v => v!.Product).ThenInclude(p => p.Unit)
            .Include(x => x.Quotes).ThenInclude(q => q.Lines).Include(x => x.Quotes).ThenInclude(q => q.Partner).Include(x => x.Quotes).ThenInclude(q => q.Order)
            .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == requestId), "Satın alma talebi bulunamadı");
        var baseCurrency = await db.Companies.Where(c => c.Id == r.CompanyId).Select(c => c.BaseCurrency).FirstAsync();
        var lineIds = r.Lines.Select(l => l.Id).ToList();
        var quotes = r.Quotes.OrderBy(q => q.CreatedAt).Select(q =>
        {
            var prices = q.Lines.Where(l => l.RequestLineId != null).Select(l => new QuotePriceDto(l.RequestLineId!.Value, l.Quantity, l.UnitPrice,
                DocumentMath.NetUnitCostBase(l.UnitPrice, l.DiscountRate, q.ExchangeRate),
                DocumentMath.Line(new LineInput(l.Quantity, l.UnitPrice, l.DiscountRate, l.VatRate)).Total)).ToList();
            var complete = lineIds.All(id => prices.Any(p => p.RequestLineId == id));
            return new ComparedQuoteDto(q.Id, q.Number, q.PartnerId, q.Partner.Title, q.QuoteDate, q.ValidUntil, q.DeliveryDays, q.CurrencyCode, q.ExchangeRate,
                q.Status, q.GrandTotal, DocumentMath.ToBase(q.GrandTotal, q.ExchangeRate), complete, q.Order?.Id, prices);
        }).ToList();
        var best = quotes.Where(q => q.Status == SupplierQuoteStatus.OPEN && q.Complete).OrderBy(q => q.GrandTotalBase).FirstOrDefault();
        return new QuoteComparisonDto(r.Id, r.Number, r.Title, r.Status, baseCurrency,
            r.Lines.OrderBy(l => l.LineNo).Select(l => new RequestLineDto(l.Id, l.LineNo, l.VariantId, l.Variant?.ProductId, l.Variant?.Sku, l.Variant?.Product.Unit.Code,
                l.Description, l.Quantity, l.EstimatedPrice)).ToList(), quotes, best?.Id);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
