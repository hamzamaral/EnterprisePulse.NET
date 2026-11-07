using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Inventory;
using EnterprisePulse.Core.Modules.Partners;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Trade;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Sales;

public sealed record CreateSalesInvoice(
    Guid PartnerId, Guid? OrderId, DateOnly InvoiceDate, DateOnly? DueDate, string? CurrencyCode, decimal? ExchangeRate, string? Notes, List<TradeLineInput> Lines);

public sealed record SalesInvoiceLineDto(
    Guid Id, int LineNo, Guid? VariantId, string? Sku, string Description, decimal Quantity, decimal ReturnedQty, decimal UnitPrice, decimal DiscountRate,
    decimal VatRate, decimal LineTotal);

public sealed record SalesInvoiceDetail(
    Guid Id, string Number, Guid PartnerId, string PartnerCode, string PartnerTitle, Guid? OrderId, string? OrderNumber, DateOnly InvoiceDate, DateOnly DueDate,
    string CurrencyCode, decimal ExchangeRate, InvoiceStatus Status, PaymentStatus PaymentStatus, decimal PaidAmount, decimal OpenAmount, bool IsOverdue,
    string? Notes, bool CanCancel, TotalsDto Totals, List<SalesInvoiceLineDto> Lines);

/// <summary>Satış faturası (FTR). Goods can be invoiced up to the delivered quantity; the customer is debited.</summary>
[ModuleService]
public sealed class SalesInvoiceService(AppDbContext db, NumberingService numbering, TradeService trade, PartnerLedgerService ledger)
{
    public async Task<SalesInvoiceDetail> CreateAsync(CreateSalesInvoice dto)
    {
        Guard.That(dto.Lines is { Count: > 0 }, "Fatura satırı gerekli");
        await using var tx = await db.Database.BeginTransactionAsync();
        SalesOrder? order = null;
        Dictionary<Guid, SalesOrderLine> orderLines = [];
        if (dto.OrderId is { } orderId)
        {
            await db.Database.ExecuteSqlAsync($"SELECT id FROM sales_orders WHERE id = {orderId} FOR UPDATE");
            order = Guard.Found(await db.SalesOrders.Include(o => o.Lines).ThenInclude(l => l.Variant).ThenInclude(v => v.Product)
                .FirstOrDefaultAsync(o => o.Id == orderId), "Satış siparişi bulunamadı");
            Guard.State(order.Status is SalesOrderStatus.APPROVED or SalesOrderStatus.PARTIALLY_DELIVERED or SalesOrderStatus.DELIVERED, "Sipariş faturalanamaz");
            Guard.That(order.PartnerId == dto.PartnerId, "Fatura carisi sipariş carisiyle aynı olmalı");
            orderLines = order.Lines.ToDictionary(l => l.Id);
        }
        var inputs = new List<TradeLineInput>();
        var invoicing = new Dictionary<Guid, decimal>();
        for (var i = 0; i < dto.Lines.Count; i++)
        {
            var input = dto.Lines[i];
            if (input.OrderLineId is not { } lineId)
            {
                inputs.Add(input);
                continue;
            }
            Guard.That(orderLines.TryGetValue(lineId, out var line), $"Satır {i + 1}: sipariş satırı bu siparişte yok");
            var stocked = line!.Variant.Product.Type == ProductType.GOODS;
            var cap = stocked ? line.DeliveredQty : line.Quantity;
            var open = Math.Max(0, cap - line.InvoicedQty);
            var total = invoicing.GetValueOrDefault(line.Id) + input.Quantity;
            Guard.That(total <= open + 0.0005m, $"Satır {i + 1}: en fazla {StockMovementService.Qty(open)} faturalanabilir");
            invoicing[line.Id] = total;
            inputs.Add(input with { VariantId = line.VariantId });
        }

        var partner = await ledger.ForDocumentAsync(dto.PartnerId, PartnerSides.Customers);
        var date = dto.InvoiceDate == default ? DateOnly.FromDateTime(DateTime.UtcNow) : dto.InvoiceDate;
        var due = dto.DueDate ?? date.AddDays(partner.PaymentTermDays);
        Guard.That(due >= date, "Vade fatura tarihinden önce olamaz");
        var currency = await trade.ResolveCurrencyAsync(dto.CurrencyCode ?? order?.CurrencyCode ?? partner.CurrencyCode, dto.ExchangeRate, date);
        var variants = await trade.LoadVariantsAsync(inputs.Select(l => l.VariantId));
        var priced = TradeService.PriceLines(inputs, variants);

        var invoice = new SalesInvoice
        {
            Number = await numbering.NextAsync("SALES_INVOICE"),
            PartnerId = partner.Id, OrderId = order?.Id, InvoiceDate = date, DueDate = due, CurrencyCode = currency.CurrencyCode, ExchangeRate = currency.ExchangeRate,
            Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim(),
            Subtotal = priced.Totals.Subtotal, DiscountTotal = priced.Totals.DiscountTotal, VatTotal = priced.Totals.VatTotal, GrandTotal = priced.Totals.GrandTotal,
            CreatedById = db.User.RequireUserId(),
        };
        foreach (var l in priced.Lines)
        {
            invoice.Lines.Add(new SalesInvoiceLine
            {
                LineNo = l.LineNo, OrderLineId = l.Input.OrderLineId, VariantId = l.VariantId, Description = l.Description, Quantity = l.Quantity,
                UnitPrice = l.UnitPrice, DiscountRate = l.DiscountRate, VatRate = l.VatRate, LineTotal = l.LineTotal,
            });
        }
        db.SalesInvoices.Add(invoice);
        foreach (var (lineId, qty) in invoicing) orderLines[lineId].InvoicedQty += qty;
        await db.SaveChangesAsync();
        if (priced.Totals.GrandTotal > 0)
        {
            await ledger.PostAsync(new LedgerPosting(partner.Id, date, due, LedgerDocumentType.SALES_INVOICE, invoice.Id, invoice.Number,
                $"Satış faturası {invoice.Number}", priced.Totals.GrandTotal, 0, currency.CurrencyCode, currency.ExchangeRate));
        }
        await tx.CommitAsync();
        return await GetAsync(invoice.Id);
    }

    public async Task<SalesInvoiceDetail> CancelAsync(Guid id, string? reason)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlAsync($"SELECT id FROM sales_invoices WHERE id = {id} FOR UPDATE");
        var invoice = Guard.Found(await db.SalesInvoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id), "Satış faturası bulunamadı");
        Guard.State(invoice.Status == InvoiceStatus.ISSUED, "Fatura zaten iptal edilmiş");
        Guard.State(invoice.PaidAmount == 0, "Faturanın tahsilatı veya iadesi var; önce onu kaldırın");
        var einvoice = await db.EInvoices.FirstOrDefaultAsync(e => e.SalesInvoiceId == id);
        var check = Einvoice.EInvoiceRules.SalesCancelCheck(einvoice?.Profile, einvoice?.Status);
        Guard.State(check.Allowed, check.Reason ?? "Fatura iptal edilemez");
        if (einvoice != null && check.CancelEInvoice)
        {
            einvoice.Status = EInvoiceStatus.CANCELLED;
            einvoice.StatusCode = "IPTAL";
            einvoice.StatusMessage = "e-Arşiv faturası iptal edildi (iptal raporuna eklendi)";
            db.EInvoiceEvents.Add(new EInvoiceEvent { EInvoiceId = einvoice.Id, Status = EInvoiceStatus.CANCELLED, Code = "IPTAL", Message = einvoice.StatusMessage, UserId = db.User.UserId });
        }
        var lineIds = invoice.Lines.Where(l => l.OrderLineId != null).Select(l => l.OrderLineId!.Value).ToList();
        var orderLines = await db.SalesOrderLines.Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
        foreach (var line in invoice.Lines.Where(l => l.OrderLineId != null)) orderLines[line.OrderLineId!.Value].InvoicedQty -= line.Quantity;
        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        await ledger.ReverseAsync(LedgerDocumentType.SALES_INVOICE, invoice.Id, DateOnly.FromDateTime(DateTime.UtcNow),
            $"İptal: {invoice.Number}{(text == null ? "" : $" — {text}")}");
        invoice.Status = InvoiceStatus.CANCELLED;
        invoice.CancelledAt = DateTime.UtcNow;
        if (text != null) invoice.Notes = string.IsNullOrEmpty(invoice.Notes) ? $"[İptal] {text}" : $"{invoice.Notes}\n[İptal] {text}";
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(id);
    }

    public async Task<SalesInvoiceDetail> GetAsync(Guid id)
    {
        var i = Guard.Found(await db.SalesInvoices.AsNoTracking().Include(x => x.Partner).Include(x => x.Order)
            .Include(x => x.Lines).ThenInclude(l => l.Variant).ThenInclude(v => v!.Product).ThenInclude(p => p.Unit)
            .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id), "Satış faturası bulunamadı");
        return new SalesInvoiceDetail(i.Id, i.Number, i.PartnerId, i.Partner.Code, i.Partner.Title, i.OrderId, i.Order?.Number, i.InvoiceDate, i.DueDate,
            i.CurrencyCode, i.ExchangeRate, i.Status, i.PaymentStatus, i.PaidAmount, Math.Max(0, i.GrandTotal - i.PaidAmount),
            TradeService.IsOverdue(i.Status, i.PaymentStatus, i.DueDate), i.Notes, i.Status == InvoiceStatus.ISSUED && i.PaidAmount == 0,
            TradeService.Totals(i.Subtotal, i.DiscountTotal, i.VatTotal, i.GrandTotal, i.ExchangeRate),
            i.Lines.OrderBy(l => l.LineNo).Select(l => new SalesInvoiceLineDto(l.Id, l.LineNo, l.VariantId, l.Variant?.Sku, l.Description, l.Quantity, l.ReturnedQty,
                l.UnitPrice, l.DiscountRate, l.VatRate, l.LineTotal)).ToList());
    }
}
