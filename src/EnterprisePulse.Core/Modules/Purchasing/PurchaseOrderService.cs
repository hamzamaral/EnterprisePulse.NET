using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Partners;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Trade;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Purchasing;

public sealed record SavePurchaseOrder(
    Guid PartnerId, Guid WarehouseId, DateOnly OrderDate, DateOnly? ExpectedDate, string? CurrencyCode, decimal? ExchangeRate, string? Notes,
    List<TradeLineInput> Lines);

public sealed record OrderLineDto(
    Guid Id, int LineNo, Guid VariantId, Guid? ProductId, string? Sku, ProductType? ProductType, TrackingType? TrackingType, string? UnitCode,
    string Description, decimal Quantity, decimal ReceivedQty, decimal InvoicedQty, decimal UnitPrice, decimal DiscountRate, decimal VatRate, decimal LineTotal);

public sealed record OrderDetailDto(
    Guid Id, string Number, Guid PartnerId, string PartnerCode, string PartnerTitle, int PaymentTermDays, Guid? RequestId, string? RequestNumber,
    Guid? QuoteId, DateOnly OrderDate, DateOnly? ExpectedDate, Guid WarehouseId, string WarehouseName, string CurrencyCode, decimal ExchangeRate,
    PurchaseOrderStatus Status, string? Notes, int ReceivedPercent, TotalsDto Totals, bool CanEdit, bool CanApprove, bool CanCancel, bool CanReceive,
    bool CanInvoice, List<OrderLineDto> Lines, List<LinkedDocumentDto> Receipts, List<LinkedDocumentDto> Invoices);

public sealed record OpenOrderDto(Guid Id, string Number, Guid PartnerId, string PartnerTitle, DateOnly OrderDate, string CurrencyCode, decimal ExchangeRate, Guid WarehouseId, PurchaseOrderStatus Status, int ReceivedPercent);

/// <summary>Satın alma siparişleri (SAS). Lines change only while the order is a draft.</summary>
[ModuleService]
public sealed class PurchaseOrderService(AppDbContext db, NumberingService numbering, TradeService trade, PartnerLedgerService partners)
{
    private static readonly PurchaseOrderStatus[] Receivable = [PurchaseOrderStatus.APPROVED, PurchaseOrderStatus.PARTIALLY_RECEIVED];
    private static readonly PurchaseOrderStatus[] Invoiceable = [PurchaseOrderStatus.APPROVED, PurchaseOrderStatus.PARTIALLY_RECEIVED, PurchaseOrderStatus.RECEIVED];

    public async Task<OrderDetailDto> CreateAsync(SavePurchaseOrder dto)
    {
        var partner = await partners.ForDocumentAsync(dto.PartnerId, PartnerSides.Suppliers);
        var content = await BuildAsync(dto, partner.CurrencyCode);
        await using var tx = await db.Database.BeginTransactionAsync();
        var order = new PurchaseOrder
        {
            Number = await numbering.NextAsync("PURCHASE_ORDER"),
            PartnerId = partner.Id, Status = PurchaseOrderStatus.DRAFT, CreatedById = db.User.RequireUserId(),
        };
        Apply(order, content);
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(order.Id);
    }

    public async Task<OrderDetailDto> UpdateAsync(Guid id, SavePurchaseOrder dto)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var order = await LockAsync(id);
        Guard.State(order.Status == PurchaseOrderStatus.DRAFT, "Yalnızca taslak sipariş düzenlenebilir");
        var partner = await db.BusinessPartners.FirstAsync(p => p.Id == order.PartnerId);
        var content = await BuildAsync(dto, partner.CurrencyCode);
        db.PurchaseOrderLines.RemoveRange(order.Lines);
        order.Lines.Clear();
        Apply(order, content);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(id);
    }

    public async Task<OrderDetailDto> ApproveAsync(Guid id)
    {
        var order = Guard.Found(await db.PurchaseOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id), "Satın alma siparişi bulunamadı");
        Guard.State(order.Status == PurchaseOrderStatus.DRAFT, "Yalnızca taslak sipariş onaylanabilir");
        Guard.State(order.Lines.Count > 0, "Satırsız sipariş onaylanamaz");
        order.Status = PurchaseOrderStatus.APPROVED;
        order.ApprovedById = db.User.RequireUserId();
        order.ApprovedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    /// <summary>Cancels a draft or approved order that has no receipts and no active invoices.</summary>
    public async Task<OrderDetailDto> CancelAsync(Guid id, string? reason)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var order = await LockAsync(id);
        Guard.State(order.Status is PurchaseOrderStatus.DRAFT or PurchaseOrderStatus.APPROVED, "Teslim alınmış veya iptal edilmiş sipariş iptal edilemez");
        var receipts = await db.GoodsReceipts.AnyAsync(r => r.OrderId == id);
        var invoices = await db.PurchaseInvoices.AnyAsync(i => i.OrderId == id && i.Status != InvoiceStatus.CANCELLED);
        Guard.State(!receipts && !invoices, "Siparişin mal kabulü veya faturası var");
        order.Status = PurchaseOrderStatus.CANCELLED;
        if (!string.IsNullOrWhiteSpace(reason)) order.Notes = string.IsNullOrEmpty(order.Notes) ? $"[İptal] {reason.Trim()}" : $"{order.Notes}\n[İptal] {reason.Trim()}";
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(id);
    }

    /// <summary>Approved orders that still have something to receive or invoice.</summary>
    public async Task<List<OpenOrderDto>> OpenAsync(Guid? partnerId)
    {
        var rows = await db.PurchaseOrders.AsNoTracking().Include(o => o.Partner).Include(o => o.Lines)
            .Where(o => Invoiceable.Contains(o.Status) && (partnerId == null || o.PartnerId == partnerId)
                && o.Lines.Any(l => l.ReceivedQty < l.Quantity || l.InvoicedQty < l.Quantity))
            .OrderByDescending(o => o.OrderDate).ToListAsync();
        return rows.Select(o => new OpenOrderDto(o.Id, o.Number, o.PartnerId, o.Partner.Title, o.OrderDate, o.CurrencyCode, o.ExchangeRate,
            o.WarehouseId, o.Status, Percent(o.Lines))).ToList();
    }

    public async Task<OrderDetailDto> GetAsync(Guid id)
    {
        var o = Guard.Found(await db.PurchaseOrders.AsNoTracking().Include(x => x.Partner).Include(x => x.Warehouse).Include(x => x.Request)
            .Include(x => x.Lines).ThenInclude(l => l.Variant).ThenInclude(v => v.Product).ThenInclude(p => p.Unit)
            .Include(x => x.Receipts).Include(x => x.Invoices)
            .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id), "Satın alma siparişi bulunamadı");
        var lines = o.Lines.OrderBy(l => l.LineNo).Select(l => new OrderLineDto(l.Id, l.LineNo, l.VariantId, l.Variant.ProductId, l.Variant.Sku,
            l.Variant.Product.Type, l.Variant.Product.TrackingType, l.Variant.Product.Unit.Code, l.Description, l.Quantity, l.ReceivedQty, l.InvoicedQty,
            l.UnitPrice, l.DiscountRate, l.VatRate, l.LineTotal)).ToList();
        var openToReceive = Receivable.Contains(o.Status) && lines.Any(l => l.ReceivedQty < l.Quantity);
        var openToInvoice = Invoiceable.Contains(o.Status) && lines.Any(l => (l.ProductType == ProductType.GOODS ? l.ReceivedQty : l.Quantity) > l.InvoicedQty);
        return new OrderDetailDto(o.Id, o.Number, o.PartnerId, o.Partner.Code, o.Partner.Title, o.Partner.PaymentTermDays, o.RequestId, o.Request?.Number,
            o.QuoteId, o.OrderDate, o.ExpectedDate, o.WarehouseId, o.Warehouse.Name, o.CurrencyCode, o.ExchangeRate, o.Status, o.Notes, Percent(o.Lines),
            TradeService.Totals(o.Subtotal, o.DiscountTotal, o.VatTotal, o.GrandTotal, o.ExchangeRate),
            o.Status == PurchaseOrderStatus.DRAFT, o.Status == PurchaseOrderStatus.DRAFT,
            o.Status is PurchaseOrderStatus.DRAFT or PurchaseOrderStatus.APPROVED, openToReceive, openToInvoice, lines,
            o.Receipts.OrderBy(r => r.Number).Select(r => new LinkedDocumentDto(r.Id, r.Number, r.ReceiptDate, "POSTED", 0)).ToList(),
            o.Invoices.OrderBy(i => i.Number).Select(i => new LinkedDocumentDto(i.Id, i.Number, i.InvoiceDate, i.Status.ToString(), i.GrandTotal)).ToList());
    }

    private async Task<(PurchaseOrder Header, List<PurchaseOrderLine> Lines)> BuildAsync(SavePurchaseOrder dto, string defaultCurrency)
    {
        var warehouse = await db.Warehouses.FirstOrDefaultAsync(w => w.Id == dto.WarehouseId && w.IsActive);
        Guard.That(warehouse != null, "Depo bulunamadı");
        Guard.That(dto.ExpectedDate == null || dto.ExpectedDate >= dto.OrderDate, "Beklenen teslim sipariş tarihinden önce olamaz");
        var currency = await trade.ResolveCurrencyAsync(dto.CurrencyCode ?? defaultCurrency, dto.ExchangeRate, dto.OrderDate);
        var variants = await trade.LoadVariantsAsync(dto.Lines.Select(l => l.VariantId));
        var priced = TradeService.PriceLines(dto.Lines, variants, requireItem: true);
        var header = new PurchaseOrder
        {
            OrderDate = dto.OrderDate, ExpectedDate = dto.ExpectedDate, WarehouseId = warehouse!.Id, CurrencyCode = currency.CurrencyCode,
            ExchangeRate = currency.ExchangeRate, Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim(),
            Subtotal = priced.Totals.Subtotal, DiscountTotal = priced.Totals.DiscountTotal, VatTotal = priced.Totals.VatTotal, GrandTotal = priced.Totals.GrandTotal,
        };
        var lines = priced.Lines.Select(l => new PurchaseOrderLine
        {
            LineNo = l.LineNo, RequestLineId = l.Input.RequestLineId, VariantId = l.VariantId!.Value, Description = l.Description, Quantity = l.Quantity,
            UnitPrice = l.UnitPrice, DiscountRate = l.DiscountRate, VatRate = l.VatRate, LineTotal = l.LineTotal,
        }).ToList();
        return (header, lines);
    }

    private static void Apply(PurchaseOrder order, (PurchaseOrder Header, List<PurchaseOrderLine> Lines) content)
    {
        var h = content.Header;
        order.OrderDate = h.OrderDate;
        order.ExpectedDate = h.ExpectedDate;
        order.WarehouseId = h.WarehouseId;
        order.CurrencyCode = h.CurrencyCode;
        order.ExchangeRate = h.ExchangeRate;
        order.Notes = h.Notes;
        order.Subtotal = h.Subtotal;
        order.DiscountTotal = h.DiscountTotal;
        order.VatTotal = h.VatTotal;
        order.GrandTotal = h.GrandTotal;
        foreach (var l in content.Lines) order.Lines.Add(l);
    }

    private async Task<PurchaseOrder> LockAsync(Guid id)
    {
        await db.Database.ExecuteSqlAsync($"SELECT id FROM purchase_orders WHERE id = {id} FOR UPDATE");
        return Guard.Found(await db.PurchaseOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id), "Satın alma siparişi bulunamadı");
    }

    public static int Percent(IEnumerable<PurchaseOrderLine> lines)
    {
        var list = lines.ToList();
        var ordered = list.Sum(l => l.Quantity);
        if (ordered == 0) return 0;
        var received = list.Sum(l => Math.Min(l.ReceivedQty, l.Quantity));
        return (int)Math.Floor(received / ordered * 100m);
    }
}
