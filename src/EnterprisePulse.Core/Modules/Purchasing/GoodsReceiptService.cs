using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Inventory;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Trade;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Purchasing;

public sealed record ReceiptLineInput(Guid OrderLineId, decimal Quantity, string? LotNo, DateOnly? ExpiryDate, List<string>? SerialNos);

public sealed record CreateGoodsReceipt(Guid OrderId, Guid? WarehouseId, DateOnly ReceiptDate, string? DeliveryNoteNo, string? Notes, List<ReceiptLineInput> Lines);

public sealed record ReceiptLineDto(Guid Id, int OrderLineNo, Guid OrderLineId, Guid VariantId, string Sku, string Description, decimal Quantity, string? LotNo, DateOnly? ExpiryDate, string? SerialNos);

public sealed record GoodsReceiptDetailDto(
    Guid Id, string Number, Guid OrderId, string OrderNumber, Guid PartnerId, string PartnerTitle, Guid WarehouseId, string WarehouseName,
    DateOnly ReceiptDate, string? DeliveryNoteNo, string? Notes, Guid? StockMovementId, string? StockMovementNumber, string CreatedByName, DateTime CreatedAt,
    List<ReceiptLineDto> Lines);

/// <summary>Mal kabul: received quantities of an approved order, posted to stock as a RECEIPT.</summary>
[ModuleService]
public sealed class GoodsReceiptService(AppDbContext db, NumberingService numbering, StockMovementService stock)
{
    public async Task<GoodsReceiptDetailDto> CreateAsync(CreateGoodsReceipt dto)
    {
        Guard.That(dto.Lines is { Count: > 0 }, "En az bir satır gerekli");
        List<LowStockItem> lowStock = [];
        Guid id;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlAsync($"SELECT id FROM purchase_orders WHERE id = {dto.OrderId} FOR UPDATE");
            var order = Guard.Found(await db.PurchaseOrders.Include(o => o.Lines).ThenInclude(l => l.Variant).ThenInclude(v => v.Product)
                .FirstOrDefaultAsync(o => o.Id == dto.OrderId), "Satın alma siparişi bulunamadı");
            Guard.State(order.Status is PurchaseOrderStatus.APPROVED or PurchaseOrderStatus.PARTIALLY_RECEIVED, "Yalnızca onaylı sipariş teslim alınabilir");
            var warehouseId = dto.WarehouseId ?? order.WarehouseId;
            Guard.That(await db.Warehouses.AnyAsync(w => w.Id == warehouseId && w.IsActive), "Depo bulunamadı");

            var orderLines = order.Lines.ToDictionary(l => l.Id);
            var receiving = new Dictionary<Guid, decimal>();
            foreach (var input in dto.Lines)
            {
                Guard.That(orderLines.TryGetValue(input.OrderLineId, out var line), "Mal kabul satırı başka bir siparişe ait");
                Guard.That(input.Quantity > 0, $"Satır {line!.LineNo}: miktar sıfırdan büyük olmalı");
                var total = receiving.GetValueOrDefault(line.Id) + input.Quantity;
                var remaining = line.Quantity - line.ReceivedQty;
                Guard.That(total <= remaining + 0.0005m, $"Satır {line.LineNo}: en fazla {StockMovementService.Qty(remaining)} teslim alınabilir");
                receiving[line.Id] = total;
            }

            var note = string.IsNullOrWhiteSpace(dto.DeliveryNoteNo) ? null : dto.DeliveryNoteNo.Trim();
            var stockLines = dto.Lines.Where(i => orderLines[i.OrderLineId].Variant.Product.Type == ProductType.GOODS).Select(i =>
            {
                var line = orderLines[i.OrderLineId];
                return new MovementLineInput(line.VariantId, i.Quantity, DocumentMath.NetUnitCostBase(line.UnitPrice, line.DiscountRate, order.ExchangeRate),
                    LotNo: i.LotNo, ExpiryDate: i.ExpiryDate, SerialNos: i.SerialNos);
            }).ToList();
            Guid? movementId = null;
            if (stockLines.Count > 0)
            {
                var posted = await stock.PostAsync(new CreateMovementRequest(StockMovementType.RECEIPT, dto.ReceiptDate, null, warehouseId,
                    note == null ? order.Number : $"{order.Number} / {note}", dto.Notes, stockLines));
                movementId = posted.MovementId;
                lowStock = posted.LowStock;
            }

            var receipt = new GoodsReceipt
            {
                Number = await numbering.NextAsync("GOODS_RECEIPT"),
                OrderId = order.Id, PartnerId = order.PartnerId, WarehouseId = warehouseId, ReceiptDate = dto.ReceiptDate, DeliveryNoteNo = note,
                Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim(), StockMovementId = movementId, CreatedById = db.User.RequireUserId(),
            };
            foreach (var input in dto.Lines)
            {
                receipt.Lines.Add(new GoodsReceiptLine
                {
                    OrderLineId = input.OrderLineId, VariantId = orderLines[input.OrderLineId].VariantId, Quantity = DocumentMath.Round3(input.Quantity),
                    LotNo = string.IsNullOrWhiteSpace(input.LotNo) ? null : input.LotNo.Trim(), ExpiryDate = input.ExpiryDate,
                    SerialNos = input.SerialNos is { Count: > 0 } nos ? string.Join(", ", nos.Select(s => s.Trim())) : "",
                });
            }
            db.GoodsReceipts.Add(receipt);
            foreach (var (lineId, qty) in receiving) orderLines[lineId].ReceivedQty += qty;
            var complete = order.Lines.All(l => l.ReceivedQty >= l.Quantity);
            order.Status = complete ? PurchaseOrderStatus.RECEIVED : PurchaseOrderStatus.PARTIALLY_RECEIVED;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            id = receipt.Id;
        }
        await stock.NotifyLowStockAsync(lowStock);
        return await GetAsync(id);
    }

    public async Task<GoodsReceiptDetailDto> GetAsync(Guid id)
    {
        var r = Guard.Found(await db.GoodsReceipts.AsNoTracking().Include(x => x.Order).Include(x => x.Partner).Include(x => x.Warehouse)
            .Include(x => x.CreatedBy).Include(x => x.StockMovement)
            .Include(x => x.Lines).ThenInclude(l => l.OrderLine).Include(x => x.Lines).ThenInclude(l => l.Variant)
            .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id), "Mal kabul bulunamadı");
        return new GoodsReceiptDetailDto(r.Id, r.Number, r.OrderId, r.Order.Number, r.PartnerId, r.Partner.Title, r.WarehouseId, r.Warehouse.Name,
            r.ReceiptDate, r.DeliveryNoteNo, r.Notes, r.StockMovementId, r.StockMovement?.Number, $"{r.CreatedBy.FirstName} {r.CreatedBy.LastName}", r.CreatedAt,
            r.Lines.OrderBy(l => l.OrderLine.LineNo).Select(l => new ReceiptLineDto(l.Id, l.OrderLine.LineNo, l.OrderLineId, l.VariantId, l.Variant.Sku,
                l.OrderLine.Description, l.Quantity, l.LotNo, l.ExpiryDate, string.IsNullOrEmpty(l.SerialNos) ? null : l.SerialNos)).ToList());
    }
}
