using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Inventory;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Sales;

public sealed record DeliveryLineInput(Guid OrderLineId, decimal Quantity, string? LotNo = null, List<Guid>? SerialIds = null);

public sealed record CreateDelivery(Guid OrderId, DateOnly DeliveryDate, string? ShippingAddress, string? Carrier, string? Notes, List<DeliveryLineInput> Lines);

public sealed record DeliveryLineDto(Guid Id, Guid OrderLineId, string Sku, string Description, decimal Quantity, string? LotNo, string SerialNos);

public sealed record DeliveryDetail(
    Guid Id, string Number, Guid OrderId, string OrderNumber, Guid PartnerId, string PartnerTitle, Guid WarehouseId, string WarehouseName,
    DateOnly DeliveryDate, string? ShippingAddress, string? Carrier, string? Notes, Guid? StockMovementId, string? StockMovementNumber, List<DeliveryLineDto> Lines);

/// <summary>İrsaliye (IRS). Stocked lines leave the warehouse; this order's own reservation does not block the issue.</summary>
[ModuleService]
public sealed class DeliveryNoteService(AppDbContext db, NumberingService numbering, StockMovementService stock, SalesOrderService orders)
{
    public async Task<DeliveryDetail> CreateAsync(CreateDelivery dto)
    {
        Guard.That(dto.Lines is { Count: > 0 }, "İrsaliye satırı gerekli");
        List<LowStockItem> low = [];
        Guid id;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlAsync($"SELECT id FROM sales_orders WHERE id = {dto.OrderId} FOR UPDATE");
            var order = Guard.Found(await db.SalesOrders.Include(o => o.Lines).ThenInclude(l => l.Variant).ThenInclude(v => v.Product)
                .FirstOrDefaultAsync(o => o.Id == dto.OrderId), "Satış siparişi bulunamadı");
            Guard.State(order.Status is SalesOrderStatus.APPROVED or SalesOrderStatus.PARTIALLY_DELIVERED, "Yalnızca onaylı sipariş sevk edilir");
            var byId = order.Lines.ToDictionary(l => l.Id);
            var shipping = new List<(SalesOrderLine Line, DeliveryLineInput Input)>();
            foreach (var input in dto.Lines)
            {
                Guard.That(byId.TryGetValue(input.OrderLineId, out var line), "Satır bu siparişte yok");
                Guard.That(line!.Variant.Product.Type == ProductType.GOODS, $"{line.Variant.Sku} hizmet satırı irsaliyeye yazılmaz");
                var open = line.Quantity - line.DeliveredQty;
                Guard.That(input.Quantity > 0 && input.Quantity <= open + 0.0005m, $"{line.Variant.Sku} için en fazla {StockMovementService.Qty(open)} sevk edilebilir");
                shipping.Add((line, input));
            }

            var movementLines = shipping.Select(s => new MovementLineInput(s.Line.VariantId, s.Input.Quantity, LotNo: s.Input.LotNo, SerialIds: s.Input.SerialIds)).ToList();
            var posted = await stock.PostAsync(new CreateMovementRequest(StockMovementType.ISSUE, dto.DeliveryDate, order.WarehouseId, null,
                null, dto.Notes, movementLines), order.Id);
            low = posted.LowStock;

            var note = new DeliveryNote
            {
                Number = await numbering.NextAsync("DELIVERY_NOTE"),
                OrderId = order.Id, PartnerId = order.PartnerId, WarehouseId = order.WarehouseId, DeliveryDate = dto.DeliveryDate,
                ShippingAddress = Blank(dto.ShippingAddress), Carrier = Blank(dto.Carrier), Notes = Blank(dto.Notes),
                StockMovementId = posted.MovementId, CreatedById = db.User.RequireUserId(),
            };
            foreach (var (line, input) in shipping)
            {
                var serialText = await SerialTextAsync(input.SerialIds);
                note.Lines.Add(new DeliveryNoteLine
                {
                    OrderLineId = line.Id, VariantId = line.VariantId, Quantity = input.Quantity, LotNo = Blank(input.LotNo), SerialNos = serialText,
                });
                line.DeliveredQty += input.Quantity;
                var reservation = await db.StockReservations.FirstOrDefaultAsync(r => r.OrderLineId == line.Id);
                if (reservation != null)
                {
                    reservation.Quantity -= input.Quantity;
                    if (reservation.Quantity <= 0.0005m) db.StockReservations.Remove(reservation);
                }
            }
            db.DeliveryNotes.Add(note);
            await db.SaveChangesAsync();
            await orders.RefreshStatusAsync(order.Id);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            id = note.Id;
        }
        await stock.NotifyLowStockAsync(low);
        return await GetAsync(id);
    }

    public async Task<DeliveryDetail> GetAsync(Guid id)
    {
        var d = Guard.Found(await db.DeliveryNotes.AsNoTracking().Include(x => x.Order).Include(x => x.Partner).Include(x => x.Warehouse)
            .Include(x => x.StockMovement).Include(x => x.Lines).ThenInclude(l => l.OrderLine).Include(x => x.Lines).ThenInclude(l => l.Variant)
            .FirstOrDefaultAsync(x => x.Id == id), "İrsaliye bulunamadı");
        return new DeliveryDetail(d.Id, d.Number, d.OrderId, d.Order.Number, d.PartnerId, d.Partner.Title, d.WarehouseId, d.Warehouse.Name, d.DeliveryDate,
            d.ShippingAddress, d.Carrier, d.Notes, d.StockMovementId, d.StockMovement?.Number,
            d.Lines.Select(l => new DeliveryLineDto(l.Id, l.OrderLineId, l.Variant.Sku, l.OrderLine.Description, l.Quantity, l.LotNo, l.SerialNos)).ToList());
    }

    private async Task<string> SerialTextAsync(List<Guid>? ids)
    {
        if (ids is not { Count: > 0 }) return "";
        var nos = await db.SerialNumbers.Where(s => ids.Contains(s.Id)).Select(s => s.SerialNo).ToListAsync();
        return string.Join(", ", nos);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
