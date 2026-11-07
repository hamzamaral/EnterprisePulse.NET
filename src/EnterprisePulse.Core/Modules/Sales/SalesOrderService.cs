using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Inventory;
using EnterprisePulse.Core.Modules.Partners;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Trade;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Sales;

public sealed record SaveSalesOrder(
    Guid PartnerId, Guid WarehouseId, DateOnly OrderDate, DateOnly? DeliveryDate, string? CustomerRef, string? CurrencyCode, decimal? ExchangeRate,
    string? Notes, List<TradeLineInput> Lines);

public sealed record SalesOrderLineDto(
    Guid Id, int LineNo, Guid VariantId, Guid ProductId, string Sku, ProductType ProductType, bool Stocked, string Description, decimal Quantity,
    decimal DeliveredQty, decimal InvoicedQty, decimal UnitPrice, decimal DiscountRate, decimal VatRate, decimal LineTotal, decimal RemainingQty, decimal InvoiceableQty);

public sealed record SalesOrderDetail(
    Guid Id, string Number, Guid PartnerId, string PartnerCode, string PartnerTitle, Guid WarehouseId, string WarehouseName, DateOnly OrderDate,
    DateOnly? DeliveryDate, string? CustomerRef, string CurrencyCode, decimal ExchangeRate, SalesOrderStatus Status, string? Notes, Guid? QuoteId,
    int DeliveredPercent, bool CanDeliver, bool CanInvoice, bool CanCancel, bool CanEdit, TotalsDto Totals, List<SalesOrderLineDto> Lines);

/// <summary>Satış siparişleri (SPS). Approval reserves goods and checks the customer credit limit.</summary>
[ModuleService]
public sealed class SalesOrderService(AppDbContext db, NumberingService numbering, TradeService trade, PartnerLedgerService partners)
{
    public async Task<SalesOrderDetail> CreateAsync(SaveSalesOrder dto) => await GetAsync((await CreateEntityAsync(dto, null)).Id);

    public async Task<SalesOrder> CreateEntityAsync(SaveSalesOrder dto, Guid? quoteId)
    {
        var prepared = await PrepareAsync(dto);
        var order = new SalesOrder { Number = await numbering.NextAsync("SALES_ORDER"), QuoteId = quoteId, CreatedById = db.User.RequireUserId() };
        Apply(order, prepared);
        foreach (var line in prepared.Priced.Lines) order.Lines.Add(Line(order.Id, line));
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    public async Task<SalesOrderDetail> UpdateAsync(Guid id, SaveSalesOrder dto)
    {
        var order = await DraftAsync(id);
        var prepared = await PrepareAsync(dto);
        Apply(order, prepared);
        db.SalesOrderLines.RemoveRange(order.Lines);
        await db.SaveChangesAsync();
        foreach (var line in prepared.Priced.Lines) db.SalesOrderLines.Add(Line(order.Id, line));
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<SalesOrderDetail> ApproveAsync(Guid id)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlAsync($"SELECT id FROM sales_orders WHERE id = {id} FOR UPDATE");
        var order = await DraftAsync(id);
        var lines = await db.SalesOrderLines.Include(l => l.Variant).ThenInclude(v => v.Product).Where(l => l.OrderId == id).ToListAsync();
        foreach (var line in lines.Where(l => l.Variant.Product.Type == ProductType.GOODS))
        {
            var onHand = await db.StockBalances.Where(b => b.WarehouseId == order.WarehouseId && b.VariantId == line.VariantId).Select(b => (decimal?)b.Quantity).FirstOrDefaultAsync() ?? 0;
            var reserved = await db.StockReservations.Where(r => r.WarehouseId == order.WarehouseId && r.VariantId == line.VariantId).SumAsync(r => (decimal?)r.Quantity) ?? 0;
            var free = onHand - reserved;
            Guard.That(free + 0.0005m >= line.Quantity, $"{line.Variant.Sku} için ayrılabilir stok {StockMovementService.Qty(free)}; sipariş {line.Quantity}");
            db.StockReservations.Add(new StockReservation { WarehouseId = order.WarehouseId, VariantId = line.VariantId, OrderLineId = line.Id, Quantity = line.Quantity });
        }
        var partner = await db.BusinessPartners.FirstAsync(p => p.Id == order.PartnerId);
        if (partner.CreditLimit is { } limit)
        {
            var exposure = await partners.BalanceAsync(partner.Id) + DocumentMath.ToBase(order.GrandTotal, order.ExchangeRate);
            Guard.That(exposure <= limit, $"{partner.Title} kredi limiti aşılıyor ({limit:N2})");
        }
        order.Status = SalesOrderStatus.APPROVED;
        order.ApprovedById = db.User.RequireUserId();
        order.ApprovedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(id);
    }

    public async Task<SalesOrderDetail> CancelAsync(Guid id)
    {
        var order = Guard.Found(await db.SalesOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id), "Satış siparişi bulunamadı");
        Guard.State(order.Status is SalesOrderStatus.DRAFT or SalesOrderStatus.APPROVED, "Sipariş bu durumda iptal edilemez");
        Guard.State(!await db.DeliveryNotes.AnyAsync(d => d.OrderId == id), "İrsaliyesi olan sipariş iptal edilemez");
        Guard.State(!await db.SalesInvoices.AnyAsync(i => i.OrderId == id && i.Status != InvoiceStatus.CANCELLED), "Faturası olan sipariş iptal edilemez");
        var lineIds = order.Lines.Select(l => l.Id).ToList();
        await db.StockReservations.Where(r => lineIds.Contains(r.OrderLineId)).ExecuteDeleteAsync();
        order.Status = SalesOrderStatus.CANCELLED;
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<List<SalesOrderDetail>> OpenAsync()
    {
        var ids = await db.SalesOrders.Where(o => o.Status == SalesOrderStatus.APPROVED || o.Status == SalesOrderStatus.PARTIALLY_DELIVERED || o.Status == SalesOrderStatus.DELIVERED)
            .Select(o => o.Id).ToListAsync();
        var result = new List<SalesOrderDetail>();
        foreach (var orderId in ids)
        {
            var detail = await GetAsync(orderId);
            if (detail.Lines.Any(l => l.DeliveredQty < l.Quantity || l.InvoicedQty < l.Quantity)) result.Add(detail);
        }
        return result;
    }

    public async Task<SalesOrderDetail> GetAsync(Guid id)
    {
        var o = Guard.Found(await db.SalesOrders.AsNoTracking().Include(x => x.Partner).Include(x => x.Warehouse)
            .Include(x => x.Lines).ThenInclude(l => l.Variant).ThenInclude(v => v.Product).FirstOrDefaultAsync(x => x.Id == id), "Satış siparişi bulunamadı");
        var lines = o.Lines.OrderBy(l => l.LineNo).Select(l =>
        {
            var stocked = l.Variant.Product.Type == ProductType.GOODS;
            var remaining = Math.Max(0, l.Quantity - l.DeliveredQty);
            var cap = stocked ? l.DeliveredQty : l.Quantity;
            return new SalesOrderLineDto(l.Id, l.LineNo, l.VariantId, l.Variant.ProductId, l.Variant.Sku, l.Variant.Product.Type, stocked, l.Description,
                l.Quantity, l.DeliveredQty, l.InvoicedQty, l.UnitPrice, l.DiscountRate, l.VatRate, l.LineTotal, remaining, Math.Max(0, cap - l.InvoicedQty));
        }).ToList();
        var qty = lines.Sum(l => l.Quantity);
        var delivered = lines.Sum(l => Math.Min(l.DeliveredQty, l.Quantity));
        var percent = qty == 0 ? 0 : (int)Math.Floor(delivered / qty * 100);
        var live = o.Status is SalesOrderStatus.APPROVED or SalesOrderStatus.PARTIALLY_DELIVERED;
        return new SalesOrderDetail(o.Id, o.Number, o.PartnerId, o.Partner.Code, o.Partner.Title, o.WarehouseId, o.Warehouse.Name, o.OrderDate, o.DeliveryDate,
            o.CustomerRef, o.CurrencyCode, o.ExchangeRate, o.Status, o.Notes, o.QuoteId, percent,
            live && lines.Any(l => l.Stocked && l.RemainingQty > 0),
            (live || o.Status == SalesOrderStatus.DELIVERED) && lines.Any(l => l.InvoiceableQty > 0),
            o.Status is SalesOrderStatus.DRAFT or SalesOrderStatus.APPROVED, o.Status == SalesOrderStatus.DRAFT,
            TradeService.Totals(o.Subtotal, o.DiscountTotal, o.VatTotal, o.GrandTotal, o.ExchangeRate), lines);
    }

    public async Task RefreshStatusAsync(Guid id)
    {
        var order = await db.SalesOrders.Include(o => o.Lines).FirstAsync(o => o.Id == id);
        if (order.Status is SalesOrderStatus.CANCELLED or SalesOrderStatus.DRAFT) return;
        var all = order.Lines.All(l => l.DeliveredQty >= l.Quantity);
        var any = order.Lines.Any(l => l.DeliveredQty > 0);
        order.Status = all ? SalesOrderStatus.DELIVERED : any ? SalesOrderStatus.PARTIALLY_DELIVERED : SalesOrderStatus.APPROVED;
    }

    private async Task<SalesOrder> DraftAsync(Guid id)
    {
        var order = Guard.Found(await db.SalesOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id), "Satış siparişi bulunamadı");
        Guard.State(order.Status == SalesOrderStatus.DRAFT, "Yalnızca taslak sipariş düzenlenir");
        return order;
    }

    private async Task<(BusinessPartner Partner, Warehouse Warehouse, CurrencyChoice Currency, DateOnly Date, SaveSalesOrder Body, PricedDocument Priced)> PrepareAsync(SaveSalesOrder dto)
    {
        var partner = await partners.ForDocumentAsync(dto.PartnerId, PartnerSides.Customers);
        var warehouse = Guard.Found(await db.Warehouses.FirstOrDefaultAsync(w => w.Id == dto.WarehouseId && w.IsActive), "Depo bulunamadı");
        var date = dto.OrderDate == default ? DateOnly.FromDateTime(DateTime.UtcNow) : dto.OrderDate;
        var currency = await trade.ResolveCurrencyAsync(dto.CurrencyCode ?? partner.CurrencyCode, dto.ExchangeRate, date);
        var variants = await trade.LoadVariantsAsync(dto.Lines.Select(l => l.VariantId));
        return (partner, warehouse, currency, date, dto, TradeService.PriceLines(dto.Lines, variants, requireItem: true));
    }

    private static void Apply(SalesOrder order, (BusinessPartner Partner, Warehouse Warehouse, CurrencyChoice Currency, DateOnly Date, SaveSalesOrder Body, PricedDocument Priced) p)
    {
        order.PartnerId = p.Partner.Id;
        order.WarehouseId = p.Warehouse.Id;
        order.OrderDate = p.Date;
        order.DeliveryDate = p.Body.DeliveryDate;
        order.CustomerRef = string.IsNullOrWhiteSpace(p.Body.CustomerRef) ? null : p.Body.CustomerRef.Trim();
        order.CurrencyCode = p.Currency.CurrencyCode;
        order.ExchangeRate = p.Currency.ExchangeRate;
        order.Notes = string.IsNullOrWhiteSpace(p.Body.Notes) ? null : p.Body.Notes.Trim();
        order.Subtotal = p.Priced.Totals.Subtotal;
        order.DiscountTotal = p.Priced.Totals.DiscountTotal;
        order.VatTotal = p.Priced.Totals.VatTotal;
        order.GrandTotal = p.Priced.Totals.GrandTotal;
    }

    private static SalesOrderLine Line(Guid orderId, PricedLine line) => new()
    {
        OrderId = orderId, LineNo = line.LineNo, VariantId = line.VariantId!.Value, Description = line.Description, Quantity = line.Quantity,
        UnitPrice = line.UnitPrice, DiscountRate = line.DiscountRate, VatRate = line.VatRate, LineTotal = line.LineTotal,
    };

}
