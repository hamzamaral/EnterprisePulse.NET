using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Inventory;
using EnterprisePulse.Core.Modules.Partners;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Trade;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Purchasing;

public sealed record CreatePurchaseInvoice(
    Guid PartnerId, Guid? OrderId, string SupplierInvoiceNo, DateOnly InvoiceDate, DateOnly? DueDate, string? CurrencyCode, decimal? ExchangeRate,
    string? Notes, List<TradeLineInput> Lines);

public sealed record InvoiceDetailDto(
    Guid Id, string Number, string SupplierInvoiceNo, Guid PartnerId, string PartnerCode, string PartnerTitle, Guid? OrderId, string? OrderNumber,
    DateOnly InvoiceDate, DateOnly DueDate, string CurrencyCode, decimal ExchangeRate, InvoiceStatus Status, PaymentStatus PaymentStatus, decimal PaidAmount,
    bool Overdue, string? Notes, DateTime? CancelledAt, bool CanCancel, TotalsDto Totals, List<TradeLineDto> Lines);

/// <summary>Alış faturaları, matched against received quantities and credited to the supplier.</summary>
[ModuleService]
public sealed class PurchaseInvoiceService(AppDbContext db, NumberingService numbering, TradeService trade, PartnerLedgerService ledger)
{
    private static readonly PurchaseOrderStatus[] Invoiceable = [PurchaseOrderStatus.APPROVED, PurchaseOrderStatus.PARTIALLY_RECEIVED, PurchaseOrderStatus.RECEIVED];

    public async Task<InvoiceDetailDto> CreateAsync(CreatePurchaseInvoice dto)
    {
        var supplierNo = dto.SupplierInvoiceNo?.Trim() ?? "";
        Guard.That(supplierNo.Length > 0, "Tedarikçi fatura numarası gerekli");
        var partner = await ledger.ForDocumentAsync(dto.PartnerId, PartnerSides.Suppliers);
        await using var tx = await db.Database.BeginTransactionAsync();
        var duplicate = await db.PurchaseInvoices.AnyAsync(i => i.PartnerId == partner.Id && i.SupplierInvoiceNo == supplierNo && i.Status != InvoiceStatus.CANCELLED);
        Guard.State(!duplicate, $"{supplierNo} numaralı fatura zaten kayıtlı");

        PurchaseOrder? order = null;
        if (dto.OrderId is { } orderId)
        {
            await db.Database.ExecuteSqlAsync($"SELECT id FROM purchase_orders WHERE id = {orderId} FOR UPDATE");
            order = await db.PurchaseOrders.Include(o => o.Lines).ThenInclude(l => l.Variant).ThenInclude(v => v.Product).FirstOrDefaultAsync(o => o.Id == orderId);
            Guard.That(order != null, "Satın alma siparişi bulunamadı");
            Guard.That(order!.PartnerId == partner.Id, "Sipariş başka bir tedarikçiye ait");
            Guard.State(Invoiceable.Contains(order.Status), "Yalnızca onaylı sipariş faturalanabilir");
        }
        var orderLines = order?.Lines.ToDictionary(l => l.Id) ?? [];
        var invoicing = new Dictionary<Guid, decimal>();
        var inputs = new List<TradeLineInput>();
        for (var i = 0; i < dto.Lines.Count; i++)
        {
            var input = dto.Lines[i];
            if (input.OrderLineId is not { } lineId)
            {
                inputs.Add(input);
                continue;
            }
            Guard.That(orderLines.TryGetValue(lineId, out var line), $"Satır {i + 1}: sipariş satırı bu siparişte yok");
            var total = invoicing.GetValueOrDefault(line!.Id) + input.Quantity;
            var limit = line.Variant.Product.Type == ProductType.GOODS ? line.ReceivedQty : line.Quantity;
            var open = Math.Max(0, limit - line.InvoicedQty);
            Guard.That(total <= open + 0.0005m, $"Satır {i + 1}: en fazla {StockMovementService.Qty(open)} faturalanabilir");
            invoicing[line.Id] = total;
            inputs.Add(input with { VariantId = line.VariantId });
        }

        var due = dto.DueDate ?? dto.InvoiceDate.AddDays(partner.PaymentTermDays);
        Guard.That(due >= dto.InvoiceDate, "Vade fatura tarihinden önce olamaz");
        var currency = await trade.ResolveCurrencyAsync(dto.CurrencyCode ?? order?.CurrencyCode ?? partner.CurrencyCode, dto.ExchangeRate, dto.InvoiceDate);
        var variants = await trade.LoadVariantsAsync(inputs.Select(l => l.VariantId));
        var priced = TradeService.PriceLines(inputs, variants);

        var invoice = new PurchaseInvoice
        {
            Number = await numbering.NextAsync("PURCHASE_INVOICE"),
            SupplierInvoiceNo = supplierNo, PartnerId = partner.Id, OrderId = order?.Id, InvoiceDate = dto.InvoiceDate, DueDate = due,
            CurrencyCode = currency.CurrencyCode, ExchangeRate = currency.ExchangeRate, Status = InvoiceStatus.ISSUED,
            Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim(),
            Subtotal = priced.Totals.Subtotal, DiscountTotal = priced.Totals.DiscountTotal, VatTotal = priced.Totals.VatTotal, GrandTotal = priced.Totals.GrandTotal,
            CreatedById = db.User.RequireUserId(),
        };
        foreach (var l in priced.Lines)
        {
            invoice.Lines.Add(new PurchaseInvoiceLine
            {
                LineNo = l.LineNo, OrderLineId = l.Input.OrderLineId, VariantId = l.VariantId, Description = l.Description, Quantity = l.Quantity,
                UnitPrice = l.UnitPrice, DiscountRate = l.DiscountRate, VatRate = l.VatRate, LineTotal = l.LineTotal,
            });
        }
        db.PurchaseInvoices.Add(invoice);
        foreach (var (lineId, qty) in invoicing) orderLines[lineId].InvoicedQty += qty;
        await db.SaveChangesAsync();
        if (priced.Totals.GrandTotal > 0)
        {
            await ledger.PostAsync(new LedgerPosting(partner.Id, dto.InvoiceDate, due, LedgerDocumentType.PURCHASE_INVOICE, invoice.Id, invoice.Number,
                $"Alış faturası {supplierNo}", 0, priced.Totals.GrandTotal, currency.CurrencyCode, currency.ExchangeRate));
        }
        await tx.CommitAsync();
        return await GetAsync(invoice.Id);
    }

    /// <summary>Cancels an unpaid invoice: the ledger entry is reversed and the order lines reopen.</summary>
    public async Task<InvoiceDetailDto> CancelAsync(Guid id, string? reason)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlAsync($"SELECT id FROM purchase_invoices WHERE id = {id} FOR UPDATE");
        var invoice = Guard.Found(await db.PurchaseInvoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id), "Alış faturası bulunamadı");
        Guard.State(invoice.Status == InvoiceStatus.ISSUED, "Fatura zaten iptal edilmiş");
        Guard.State(invoice.PaidAmount == 0, "Faturanın ödemesi var; önce ödemeyi kaldırın");
        var lineIds = invoice.Lines.Where(l => l.OrderLineId != null).Select(l => l.OrderLineId!.Value).ToList();
        var orderLines = await db.PurchaseOrderLines.Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
        foreach (var line in invoice.Lines.Where(l => l.OrderLineId != null)) orderLines[line.OrderLineId!.Value].InvoicedQty -= line.Quantity;
        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        await ledger.ReverseAsync(LedgerDocumentType.PURCHASE_INVOICE, invoice.Id, DateOnly.FromDateTime(DateTime.UtcNow),
            $"İptal: {invoice.Number}{(text == null ? "" : $" — {text}")}");
        invoice.Status = InvoiceStatus.CANCELLED;
        invoice.CancelledAt = DateTime.UtcNow;
        if (text != null) invoice.Notes = string.IsNullOrEmpty(invoice.Notes) ? $"[İptal] {text}" : $"{invoice.Notes}\n[İptal] {text}";
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(id);
    }

    public async Task<InvoiceDetailDto> GetAsync(Guid id)
    {
        var i = Guard.Found(await db.PurchaseInvoices.AsNoTracking().Include(x => x.Partner).Include(x => x.Order)
            .Include(x => x.Lines).ThenInclude(l => l.Variant).ThenInclude(v => v!.Product).ThenInclude(p => p.Unit)
            .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id), "Alış faturası bulunamadı");
        return new InvoiceDetailDto(i.Id, i.Number, i.SupplierInvoiceNo, i.PartnerId, i.Partner.Code, i.Partner.Title, i.OrderId, i.Order?.Number,
            i.InvoiceDate, i.DueDate, i.CurrencyCode, i.ExchangeRate, i.Status, i.PaymentStatus, i.PaidAmount,
            TradeService.IsOverdue(i.Status, i.PaymentStatus, i.DueDate), i.Notes, i.CancelledAt, i.Status == InvoiceStatus.ISSUED && i.PaidAmount == 0,
            TradeService.Totals(i.Subtotal, i.DiscountTotal, i.VatTotal, i.GrandTotal, i.ExchangeRate),
            i.Lines.OrderBy(l => l.LineNo).Select(l => TradeService.LineDto(l.Id, l.LineNo, l.Variant, l.Description, l.Quantity, l.UnitPrice, l.DiscountRate,
                l.VatRate, l.LineTotal)).ToList());
    }
}
