using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Finance;
using EnterprisePulse.Core.Modules.Inventory;
using EnterprisePulse.Core.Modules.Partners;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Trade;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Sales;

public sealed record ReturnLineInput(Guid InvoiceLineId, decimal Quantity, string? LotNo = null, List<string>? SerialNos = null);

public sealed record CreateSalesReturn(Guid InvoiceId, Guid WarehouseId, DateOnly ReturnDate, string Reason, List<ReturnLineInput> Lines);

public sealed record SalesReturnDetail(
    Guid Id, string Number, Guid InvoiceId, string InvoiceNumber, Guid PartnerId, string PartnerTitle, Guid WarehouseId, string WarehouseName,
    DateOnly ReturnDate, string Reason, string CurrencyCode, decimal ExchangeRate, decimal GrandTotal, Guid? StockMovementId, string? StockMovementNumber);

/// <summary>Satış iadesi (IAD). Goods come back at the invoiced net cost; the customer is credited and the invoice's paid amount grows.</summary>
[ModuleService]
public sealed class SalesReturnService(AppDbContext db, NumberingService numbering, StockMovementService stock, PartnerLedgerService ledger)
{
    public async Task<SalesReturnDetail> CreateAsync(CreateSalesReturn dto)
    {
        Guard.That(dto.Lines is { Count: > 0 }, "İade satırı gerekli");
        Guard.That(!string.IsNullOrWhiteSpace(dto.Reason), "İade gerekçesi gerekli");
        List<LowStockItem> low = [];
        Guid id;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlAsync($"SELECT id FROM sales_invoices WHERE id = {dto.InvoiceId} FOR UPDATE");
            var invoice = Guard.Found(await db.SalesInvoices.Include(i => i.Lines).ThenInclude(l => l.Variant).ThenInclude(v => v!.Product)
                .FirstOrDefaultAsync(i => i.Id == dto.InvoiceId), "Satış faturası bulunamadı");
            Guard.State(invoice.Status == InvoiceStatus.ISSUED, "Yalnızca kesilmiş fatura iade edilir");
            var warehouse = Guard.Found(await db.Warehouses.FirstOrDefaultAsync(w => w.Id == dto.WarehouseId && w.IsActive), "Depo bulunamadı");
            var byId = invoice.Lines.ToDictionary(l => l.Id);
            var chosen = new List<(SalesInvoiceLine Line, ReturnLineInput Input, PricedLine Priced)>();
            var inputs = new List<TradeLineInput>();
            foreach (var input in dto.Lines)
            {
                Guard.That(byId.TryGetValue(input.InvoiceLineId, out var line), "Satır bu faturada yok");
                var open = line!.Quantity - line.ReturnedQty;
                Guard.That(input.Quantity > 0 && input.Quantity <= open + 0.0005m, $"{line.Description} için en fazla {StockMovementService.Qty(open)} iade edilebilir");
                inputs.Add(new TradeLineInput(line.VariantId, line.Description, input.Quantity, line.UnitPrice, line.DiscountRate, line.VatRate));
            }
            var variants = await tradeVariants(inputs);
            var priced = TradeService.PriceLines(inputs, variants);
            for (var i = 0; i < dto.Lines.Count; i++) chosen.Add((byId[dto.Lines[i].InvoiceLineId], dto.Lines[i], priced.Lines[i]));

            Guid? movementId = null;
            var stockLines = chosen.Where(c => c.Line.Variant?.Product.Type == ProductType.GOODS).ToList();
            if (stockLines.Count > 0)
            {
                var posted = await stock.PostAsync(new CreateMovementRequest(StockMovementType.RECEIPT, dto.ReturnDate, null, warehouse.Id, null, dto.Reason.Trim(),
                    stockLines.Select(c => new MovementLineInput(c.Line.VariantId!.Value, c.Input.Quantity,
                        DocumentMath.NetUnitCostBase(c.Line.UnitPrice, c.Line.DiscountRate, invoice.ExchangeRate),
                        LotNo: c.Input.LotNo, SerialNos: c.Input.SerialNos)).ToList()));
                movementId = posted.MovementId;
                low = posted.LowStock;
            }

            var doc = new SalesReturn
            {
                Number = await numbering.NextAsync("SALES_RETURN"),
                InvoiceId = invoice.Id, PartnerId = invoice.PartnerId, WarehouseId = warehouse.Id, ReturnDate = dto.ReturnDate, Reason = dto.Reason.Trim(),
                CurrencyCode = invoice.CurrencyCode, ExchangeRate = invoice.ExchangeRate, StockMovementId = movementId, CreatedById = db.User.RequireUserId(),
                Subtotal = priced.Totals.Subtotal, DiscountTotal = priced.Totals.DiscountTotal, VatTotal = priced.Totals.VatTotal, GrandTotal = priced.Totals.GrandTotal,
            };
            var n = 1;
            foreach (var (line, input, price) in chosen)
            {
                doc.Lines.Add(new SalesReturnLine
                {
                    LineNo = n++, InvoiceLineId = line.Id, VariantId = line.VariantId, Description = line.Description, Quantity = input.Quantity,
                    UnitPrice = line.UnitPrice, DiscountRate = line.DiscountRate, VatRate = line.VatRate, LineTotal = price.LineTotal,
                    LotNo = string.IsNullOrWhiteSpace(input.LotNo) ? null : input.LotNo.Trim(),
                    SerialNos = input.SerialNos is { Count: > 0 } nos ? string.Join(", ", nos.Select(s => s.Trim())) : "",
                });
                line.ReturnedQty += input.Quantity;
            }
            db.SalesReturns.Add(doc);
            InvoicePayments.Apply(invoice, priced.Totals.GrandTotal);
            await db.SaveChangesAsync();
            if (priced.Totals.GrandTotal > 0)
            {
                await ledger.PostAsync(new LedgerPosting(invoice.PartnerId, dto.ReturnDate, null, LedgerDocumentType.SALES_RETURN, doc.Id, doc.Number,
                    $"Satış iadesi {doc.Number}", 0, priced.Totals.GrandTotal, invoice.CurrencyCode, invoice.ExchangeRate));
            }
            await tx.CommitAsync();
            id = doc.Id;
        }
        await stock.NotifyLowStockAsync(low);
        return await GetAsync(id);
    }

    public async Task<SalesReturnDetail> GetAsync(Guid id)
    {
        var r = Guard.Found(await db.SalesReturns.AsNoTracking().Include(x => x.Invoice).Include(x => x.Partner).Include(x => x.Warehouse).Include(x => x.StockMovement)
            .FirstOrDefaultAsync(x => x.Id == id), "Satış iadesi bulunamadı");
        return new SalesReturnDetail(r.Id, r.Number, r.InvoiceId, r.Invoice.Number, r.PartnerId, r.Partner.Title, r.WarehouseId, r.Warehouse.Name,
            r.ReturnDate, r.Reason, r.CurrencyCode, r.ExchangeRate, r.GrandTotal, r.StockMovementId, r.StockMovement?.Number);
    }

    private async Task<Dictionary<Guid, ProductVariant>> tradeVariants(List<TradeLineInput> inputs)
    {
        var ids = inputs.Where(l => l.VariantId != null).Select(l => l.VariantId!.Value).Distinct().ToList();
        var rows = await db.ProductVariants.Include(v => v.Product).Where(v => ids.Contains(v.Id)).ToListAsync();
        return rows.ToDictionary(v => v.Id);
    }
}
