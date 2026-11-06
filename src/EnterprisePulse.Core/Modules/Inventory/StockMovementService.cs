using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Inventory;

/// <summary>
/// One movement line. Lot items give <see cref="LotId"/> or <see cref="LotNo"/>; serial items give new numbers
/// (<see cref="SerialNos"/>) when stock comes in and existing serial ids when it goes out.
/// </summary>
public sealed record MovementLineInput(
    Guid VariantId, decimal Quantity, decimal? UnitCost = null, Guid? LotId = null, string? LotNo = null, DateOnly? ExpiryDate = null,
    List<string>? SerialNos = null, List<Guid>? SerialIds = null);

public sealed record CreateMovementRequest(
    StockMovementType Type, DateOnly MovementDate, Guid? SourceWarehouseId, Guid? TargetWarehouseId, string? Reference, string? Notes,
    List<MovementLineInput> Lines);

public sealed record LowStockItem(Guid VariantId, Guid ProductId, string Sku, string Name, decimal OnHand, decimal MinStock);

public sealed record PostResult(Guid MovementId, string Number, List<LowStockItem> LowStock);

public sealed record MovementLineDto(
    Guid Id, Guid VariantId, Guid ProductId, string Sku, string ProductName, string? VariantName, string UnitCode, decimal Quantity,
    decimal UnitCost, decimal LineValue, string? LotNo, DateOnly? ExpiryDate, List<string> SerialNos);

public sealed record MovementDetailDto(
    Guid Id, string Number, StockMovementType Type, DateOnly MovementDate, Guid? SourceWarehouseId, string? SourceWarehouseName,
    Guid? TargetWarehouseId, string? TargetWarehouseName, string? Reference, string? Notes, int LineCount, decimal TotalValue,
    string CreatedByName, DateTime CreatedAt, Guid? StockCountId, string? StockCountNumber, Guid? GoodsReceiptId, string? GoodsReceiptNumber,
    List<MovementLineDto> Lines);

/// <summary>
/// Posts stock documents. A movement is applied immediately and never edited: balances, lot balances and serials change
/// in one transaction and every change is written to the ledger (<see cref="StockTransaction"/>) with the resulting balance.
/// </summary>
[ModuleService]
public sealed class StockMovementService(AppDbContext db, NumberingService numbering, NotificationService notifications)
{
    private CurrentUser Ctx => db.User;

    public async Task<MovementDetailDto> CreateAsync(CreateMovementRequest request)
    {
        PostResult result;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            result = await PostAsync(request);
            await tx.CommitAsync();
        }
        await NotifyLowStockAsync(result.LowStock);
        return await GetAsync(result.MovementId);
    }

    /// <summary>Posts inside the caller's transaction (counts, receipts and deliveries use it too).</summary>
    public async Task<PostResult> PostAsync(CreateMovementRequest dto, Guid? reservationOrderId = null)
    {
        Guard.That(dto.Lines is { Count: > 0 }, "En az bir satır gerekli");
        Guard.That(dto.MovementDate <= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), "Hareket tarihi ileri bir tarih olamaz");
        var (source, target) = await ResolveWarehousesAsync(dto);

        // Variants are locked in a stable order so concurrent movements of one item are serialized.
        var variantIds = dto.Lines.Select(l => l.VariantId).Distinct().OrderBy(id => id).ToList();
        foreach (var id in variantIds)
        {
            await db.Database.ExecuteSqlAsync($"SELECT id FROM product_variants WHERE id = {id} FOR UPDATE");
        }
        var variants = await db.ProductVariants.Include(v => v.Product).Where(v => variantIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id);
        Guard.That(variants.Count == variantIds.Count, "Bir veya daha fazla ürün bulunamadı");
        ValidateLines(dto, variants);

        var onHand = await TotalsAsync(variantIds);
        var before = new Dictionary<Guid, decimal>(onHand);
        var average = variants.Values.ToDictionary(v => v.Id, v => v.AverageCost);

        var movement = new StockMovement
        {
            Number = await numbering.NextAsync("STOCK_MOVEMENT"),
            Type = dto.Type,
            MovementDate = dto.MovementDate,
            SourceWarehouseId = source?.Id,
            TargetWarehouseId = target?.Id,
            Reference = Clean(dto.Reference),
            Notes = Clean(dto.Notes),
            CreatedById = Ctx.RequireUserId(),
        };
        db.StockMovements.Add(movement);
        await db.SaveChangesAsync();

        foreach (var input in dto.Lines)
        {
            var variant = variants[input.VariantId];
            var incoming = IsIncoming(dto.Type, input.Quantity);
            var lotId = await ResolveLotAsync(variant, input, incoming);

            var cost = average[variant.Id];
            if (dto.Type == StockMovementType.RECEIPT)
            {
                var receiptCost = input.UnitCost ?? cost;
                average[variant.Id] = StockMath.WeightedAverageCost(onHand[variant.Id], cost, input.Quantity, receiptCost);
                cost = receiptCost;
            }

            var line = new StockMovementLine { MovementId = movement.Id, VariantId = variant.Id, LotId = lotId, Quantity = input.Quantity, UnitCost = cost };
            db.StockMovementLines.Add(line);
            await db.SaveChangesAsync();
            await MoveSerialsAsync(dto.Type, variant, input, line.Id, source, target);

            (Warehouse Warehouse, decimal Delta)[] entries = dto.Type switch
            {
                StockMovementType.ISSUE => [(source!, -input.Quantity)],
                StockMovementType.TRANSFER => [(source!, -input.Quantity), (target!, input.Quantity)],
                _ => [(target!, input.Quantity)],
            };
            foreach (var (warehouse, delta) in entries)
            {
                // Counts and adjustments record physical reality; only issues and transfers respect reservations.
                var guard = dto.Type is StockMovementType.ISSUE or StockMovementType.TRANSFER;
                await ApplyEntryAsync(movement, line, warehouse, variant, lotId, delta, cost, guard, reservationOrderId);
                onHand[variant.Id] += delta;
            }
        }

        foreach (var v in variants.Values.Where(v => v.AverageCost != average[v.Id])) v.AverageCost = average[v.Id];
        await db.SaveChangesAsync();

        var lowStock = variants.Values.Where(v => StockMath.CrossedBelowMinimum(before[v.Id], onHand[v.Id], v.MinStock))
            .Select(v => new LowStockItem(v.Id, v.ProductId, v.Sku, VariantLabel(v), onHand[v.Id], v.MinStock)).ToList();
        return new PostResult(movement.Id, movement.Number, lowStock);
    }

    /// <summary>Tells stock managers (<c>inventory:update</c>) that items fell below their reorder point.</summary>
    public async Task NotifyLowStockAsync(IReadOnlyCollection<LowStockItem> items)
    {
        if (items.Count == 0) return;
        var companyId = Ctx.RequireCompanyId();
        var users = await notifications.UsersWithPermissionAsync(companyId, "inventory:update");
        foreach (var item in items)
        {
            await notifications.NotifyAsync(new NotifyRequest(users, companyId, "inventory.low_stock", $"Kritik stok: {item.Sku}",
                $"{item.Name} stoğu {Qty(item.OnHand)} — minimum {Qty(item.MinStock)}.", $"/r/products/{item.ProductId}"));
        }
    }

    public async Task<MovementDetailDto> GetAsync(Guid id)
    {
        var m = Guard.Found(await db.StockMovements.AsNoTracking()
            .Include(x => x.SourceWarehouse).Include(x => x.TargetWarehouse).Include(x => x.CreatedBy)
            .Include(x => x.StockCount).Include(x => x.GoodsReceipt)
            .Include(x => x.Lines).ThenInclude(l => l.Variant).ThenInclude(v => v.Product).ThenInclude(p => p.Unit)
            .Include(x => x.Lines).ThenInclude(l => l.Lot)
            .Include(x => x.Lines).ThenInclude(l => l.Serials).ThenInclude(s => s.Serial)
            .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id), "Stok hareketi bulunamadı");
        var lines = m.Lines.OrderBy(l => l.Variant.Sku).Select(l => new MovementLineDto(l.Id, l.VariantId, l.Variant.ProductId, l.Variant.Sku,
            l.Variant.Product.Name, l.Variant.Name, l.Variant.Product.Unit.Code, l.Quantity, l.UnitCost ?? 0, Math.Round(l.Quantity * (l.UnitCost ?? 0), 2),
            l.Lot?.LotNo, l.Lot?.ExpiryDate, l.Serials.Select(s => s.Serial.SerialNo).Order().ToList())).ToList();
        return new MovementDetailDto(m.Id, m.Number, m.Type, m.MovementDate, m.SourceWarehouseId, m.SourceWarehouse?.Name, m.TargetWarehouseId,
            m.TargetWarehouse?.Name, m.Reference, m.Notes, lines.Count, lines.Sum(l => l.LineValue), $"{m.CreatedBy.FirstName} {m.CreatedBy.LastName}",
            m.CreatedAt, m.StockCount?.Id, m.StockCount?.Number, m.GoodsReceipt?.Id, m.GoodsReceipt?.Number, lines);
    }

    // ---------- posting helpers ----------

    private async Task<(Warehouse? Source, Warehouse? Target)> ResolveWarehousesAsync(CreateMovementRequest dto)
    {
        var (needsSource, needsTarget) = dto.Type switch
        {
            StockMovementType.ISSUE => (true, false),
            StockMovementType.TRANSFER => (true, true),
            _ => (false, true),
        };
        Guard.That(needsSource == (dto.SourceWarehouseId != null), needsSource ? "Çıkış deposu seçilmeli" : "Bu hareket türünde çıkış deposu olmaz");
        Guard.That(needsTarget == (dto.TargetWarehouseId != null), needsTarget ? "Giriş deposu seçilmeli" : "Bu hareket türünde giriş deposu olmaz");
        Guard.That(dto.SourceWarehouseId == null || dto.SourceWarehouseId != dto.TargetWarehouseId, "Çıkış ve giriş depoları farklı olmalı");
        async Task<Warehouse?> Load(Guid? id)
        {
            if (id == null) return null;
            var w = await db.Warehouses.FirstOrDefaultAsync(x => x.Id == id);
            Guard.That(w != null, "Depo bulunamadı");
            Guard.That(w!.IsActive, $"{w.Name} deposu pasif");
            return w;
        }
        return (await Load(dto.SourceWarehouseId), await Load(dto.TargetWarehouseId));
    }

    private static void ValidateLines(CreateMovementRequest dto, Dictionary<Guid, ProductVariant> variants)
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < dto.Lines.Count; i++)
        {
            var line = dto.Lines[i];
            var v = variants[line.VariantId];
            var label = $"Satır {i + 1} ({v.Sku})";
            var incoming = IsIncoming(dto.Type, line.Quantity);
            Guard.That(v.Product.Type == ProductType.GOODS, $"{label}: hizmetler stokta izlenmez");
            Guard.That(dto.Type == StockMovementType.ADJUSTMENT ? line.Quantity != 0 : line.Quantity > 0, $"{label}: geçersiz miktar");
            Guard.That(!incoming || (v.IsActive && v.Product.IsActive && v.Product.DeletedAt == null), $"{label}: ürün pasif");
            Guard.That(line.UnitCost == null || dto.Type == StockMovementType.RECEIPT, $"{label}: birim maliyet yalnızca girişte girilir");
            Guard.That(line.UnitCost is null or >= 0, $"{label}: birim maliyet negatif olamaz");

            var tracking = v.Product.TrackingType;
            var hasLot = line.LotId != null || !string.IsNullOrWhiteSpace(line.LotNo);
            var hasSerials = line.SerialNos is { Count: > 0 } || line.SerialIds is { Count: > 0 };
            Guard.That(tracking == TrackingType.LOT || (!hasLot && line.ExpiryDate == null), $"{label}: ürün lot takipli değil");
            Guard.That(tracking == TrackingType.SERIAL || !hasSerials, $"{label}: ürün seri takipli değil");
            Guard.That(tracking != TrackingType.LOT || hasLot, $"{label}: lot numarası gerekli");
            if (tracking != TrackingType.SERIAL) continue;

            Guard.That(StockMath.IsWhole(line.Quantity), $"{label}: seri takipli miktar tam sayı olmalı");
            Guard.That(!incoming || line.SerialIds is not { Count: > 0 }, $"{label}: girişte yeni seri numaraları yazılır");
            Guard.That(incoming || line.SerialNos is not { Count: > 0 }, $"{label}: çıkışta stoktaki seri numaraları seçilir");
            var serials = incoming ? (line.SerialNos ?? []).Select(s => s.Trim()).ToList() : (line.SerialIds ?? []).Select(s => s.ToString()).ToList();
            Guard.That(serials.Count == (int)Math.Abs(line.Quantity), $"{label}: {Math.Abs(line.Quantity):0.###} seri numarası gerekli");
            foreach (var s in serials)
            {
                Guard.That(s.Length > 0 && seen.Add($"{v.Id}:{s}"), $"{label}: tekrarlanan seri numarası {s}");
            }
        }
    }

    private async Task<Guid?> ResolveLotAsync(ProductVariant v, MovementLineInput line, bool incoming)
    {
        if (v.Product.TrackingType != TrackingType.LOT) return null;
        if (line.LotId is { } lotId)
        {
            Guard.That(await db.StockLots.AnyAsync(l => l.Id == lotId && l.VariantId == v.Id), $"{v.Sku} için lot bulunamadı");
            return lotId;
        }
        var lotNo = line.LotNo!.Trim();
        var lot = await db.StockLots.FirstOrDefaultAsync(l => l.VariantId == v.Id && l.LotNo == lotNo);
        if (!incoming)
        {
            Guard.That(lot != null, $"{v.Sku} için {lotNo} lotu bulunamadı");
            return lot!.Id;
        }
        if (lot == null)
        {
            lot = new StockLot { VariantId = v.Id, LotNo = lotNo, ExpiryDate = line.ExpiryDate };
            db.StockLots.Add(lot);
        }
        else if (line.ExpiryDate != null)
        {
            lot.ExpiryDate = line.ExpiryDate;
        }
        await db.SaveChangesAsync();
        return lot.Id;
    }

    private async Task MoveSerialsAsync(StockMovementType type, ProductVariant v, MovementLineInput line, Guid lineId, Warehouse? source, Warehouse? target)
    {
        if (v.Product.TrackingType != TrackingType.SERIAL) return;
        List<SerialNumber> serials;
        if (IsIncoming(type, line.Quantity))
        {
            var numbers = line.SerialNos!.Select(s => s.Trim()).ToList();
            var existing = await db.SerialNumbers.Where(s => s.VariantId == v.Id && numbers.Contains(s.SerialNo)).ToListAsync();
            if (existing.FirstOrDefault(s => s.Status == SerialStatus.IN_STOCK) is { } inStock)
            {
                throw new ConflictAppException($"{inStock.SerialNo} seri numarası zaten stokta");
            }
            foreach (var s in existing)
            {
                s.Status = SerialStatus.IN_STOCK;
                s.WarehouseId = target!.Id;
            }
            var known = existing.Select(s => s.SerialNo).ToHashSet();
            var created = numbers.Where(n => !known.Contains(n)).Select(n => new SerialNumber { VariantId = v.Id, SerialNo = n, WarehouseId = target!.Id }).ToList();
            db.SerialNumbers.AddRange(created);
            serials = [.. existing, .. created];
        }
        else
        {
            var from = type == StockMovementType.ADJUSTMENT ? target! : source!;
            var ids = line.SerialIds!;
            serials = await db.SerialNumbers.Where(s => ids.Contains(s.Id) && s.VariantId == v.Id).ToListAsync();
            Guard.That(serials.Count == ids.Count, $"{v.Sku} için bilinmeyen seri numarası");
            if (serials.FirstOrDefault(s => s.Status != SerialStatus.IN_STOCK || s.WarehouseId != from.Id) is { } unavailable)
            {
                throw new BadRequestAppException($"{unavailable.SerialNo} seri numarası {from.Name} deposunda stokta değil");
            }
            foreach (var s in serials)
            {
                if (type == StockMovementType.TRANSFER)
                {
                    s.WarehouseId = target!.Id;
                }
                else
                {
                    s.Status = SerialStatus.ISSUED;
                    s.WarehouseId = null;
                }
            }
        }
        db.StockMovementSerials.AddRange(serials.Select(s => new StockMovementSerial { LineId = lineId, SerialId = s.Id }));
        await db.SaveChangesAsync();
    }

    /// <summary>Changes a warehouse (and lot) balance, never below zero, and writes the ledger entry.</summary>
    private async Task ApplyEntryAsync(StockMovement movement, StockMovementLine line, Warehouse warehouse, ProductVariant v, Guid? lotId,
        decimal delta, decimal cost, bool guardReservations, Guid? reservationOrderId)
    {
        var balance = await db.StockBalances.FirstOrDefaultAsync(b => b.WarehouseId == warehouse.Id && b.VariantId == v.Id);
        if (delta < 0)
        {
            Guard.That(balance != null && balance.Quantity >= -delta, $"{warehouse.Name} deposunda yetersiz {v.Sku} stoğu");
            balance!.Quantity += delta;
            if (lotId != null)
            {
                var lot = await db.LotBalances.FirstOrDefaultAsync(b => b.WarehouseId == warehouse.Id && b.LotId == lotId);
                Guard.That(lot != null && lot.Quantity >= -delta, $"{warehouse.Name} deposunda seçilen lotta yetersiz {v.Sku} stoğu");
                lot!.Quantity += delta;
            }
        }
        else
        {
            if (balance == null)
            {
                balance = new StockBalance { WarehouseId = warehouse.Id, VariantId = v.Id };
                db.StockBalances.Add(balance);
            }
            balance.Quantity += delta;
            if (lotId != null)
            {
                var lot = await db.LotBalances.FirstOrDefaultAsync(b => b.WarehouseId == warehouse.Id && b.LotId == lotId);
                if (lot == null)
                {
                    lot = new LotBalance { WarehouseId = warehouse.Id, LotId = lotId.Value };
                    db.LotBalances.Add(lot);
                }
                lot.Quantity += delta;
            }
        }

        if (delta < 0 && guardReservations)
        {
            var others = await db.StockReservations
                .Where(r => r.WarehouseId == warehouse.Id && r.VariantId == v.Id && (reservationOrderId == null || r.OrderLine.OrderId != reservationOrderId))
                .SumAsync(r => (decimal?)r.Quantity) ?? 0;
            Guard.That(balance.Quantity >= others,
                $"{warehouse.Name} deposunda kullanılabilir {v.Sku} stoğu yetersiz: {Qty(others)} satış siparişlerine ayrılmış");
        }

        db.StockTransactions.Add(new StockTransaction
        {
            MovementId = movement.Id, LineId = line.Id, WarehouseId = warehouse.Id, VariantId = v.Id, LotId = lotId, Quantity = delta,
            UnitCost = cost, BalanceAfter = balance.Quantity, MovementDate = movement.MovementDate,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Dictionary<Guid, decimal>> TotalsAsync(List<Guid> variantIds)
    {
        var rows = await db.StockBalances.Where(b => variantIds.Contains(b.VariantId)).GroupBy(b => b.VariantId)
            .Select(g => new { g.Key, Total = g.Sum(b => b.Quantity) }).ToListAsync();
        var totals = variantIds.ToDictionary(id => id, _ => 0m);
        foreach (var r in rows) totals[r.Key] = r.Total;
        return totals;
    }

    /// <summary>Receipts and positive adjustments bring stock in; the rest take it out.</summary>
    public static bool IsIncoming(StockMovementType type, decimal quantity) =>
        type == StockMovementType.RECEIPT || (type == StockMovementType.ADJUSTMENT && quantity > 0);

    public static string VariantLabel(ProductVariant v) => v.Name is { Length: > 0 } n ? $"{v.Product.Name} ({n})" : v.Product.Name;

    public static string Qty(decimal v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
