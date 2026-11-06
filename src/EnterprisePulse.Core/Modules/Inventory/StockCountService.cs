using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Inventory;

public sealed record CreateStockCountRequest(Guid WarehouseId, Guid? CategoryId, DateOnly CountDate, string? Notes);

public sealed record CountLineUpdate(Guid Id, decimal? CountedQty);

public sealed record AddCountLineRequest(Guid VariantId, string? LotNo, decimal? CountedQty);

public sealed record StockCountLineDto(
    Guid Id, Guid VariantId, Guid ProductId, string Sku, string? Barcode, string ProductName, string? VariantName, string UnitCode,
    Guid? LotId, string? LotNo, decimal ExpectedQty, decimal? CountedQty, decimal? Difference);

public sealed record StockCountDetailDto(
    Guid Id, string Number, Guid WarehouseId, string WarehouseName, StockCountStatus Status, DateOnly CountDate, string? Notes,
    int LineCount, int CountedCount, int DifferenceCount, string CreatedByName, DateTime? CompletedAt, Guid? AdjustmentId,
    string? AdjustmentNumber, List<StockCountLineDto> Lines);

/// <summary>
/// Physical stock counts. Starting one snapshots the warehouse's system quantities (untracked and lot items; serial
/// items are checked by serial number instead); completing it posts one ADJUSTMENT with every counted difference.
/// </summary>
[ModuleService]
public sealed class StockCountService(AppDbContext db, NumberingService numbering, StockMovementService movements)
{
    public async Task<StockCountDetailDto> CreateAsync(CreateStockCountRequest dto)
    {
        var warehouse = await db.Warehouses.FirstOrDefaultAsync(w => w.Id == dto.WarehouseId);
        Guard.That(warehouse != null, "Depo bulunamadı");
        Guard.That(warehouse!.IsActive, "Depo pasif");
        Guard.That(dto.CategoryId == null || await db.ProductCategories.AnyAsync(c => c.Id == dto.CategoryId), "Ürün kategorisi bulunamadı");

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlAsync($"SELECT id FROM warehouses WHERE id = {warehouse.Id} FOR UPDATE");
        var open = await db.StockCounts.Where(c => c.WarehouseId == warehouse.Id && c.Status == StockCountStatus.DRAFT).Select(c => c.Number).FirstOrDefaultAsync();
        Guard.State(open == null, $"Bu depo için {open} numaralı sayım hâlâ açık");

        var cat = dto.CategoryId;
        var plain = await db.StockBalances
            .Where(b => b.WarehouseId == warehouse.Id && b.Quantity > 0 && b.Variant.Product.Type == ProductType.GOODS && b.Variant.Product.DeletedAt == null
                && b.Variant.Product.TrackingType == TrackingType.NONE && (cat == null || b.Variant.Product.CategoryId == cat))
            .Select(b => new { b.Variant.Sku, LotNo = "", b.VariantId, LotId = (Guid?)null, b.Quantity }).ToListAsync();
        var lots = await db.LotBalances
            .Where(b => b.WarehouseId == warehouse.Id && b.Quantity > 0 && b.Lot.Variant.Product.Type == ProductType.GOODS && b.Lot.Variant.Product.DeletedAt == null
                && b.Lot.Variant.Product.TrackingType == TrackingType.LOT && (cat == null || b.Lot.Variant.Product.CategoryId == cat))
            .Select(b => new { b.Lot.Variant.Sku, b.Lot.LotNo, b.Lot.VariantId, LotId = (Guid?)b.LotId, b.Quantity }).ToListAsync();

        var count = new StockCount
        {
            Number = await numbering.NextAsync("STOCK_COUNT"),
            WarehouseId = warehouse.Id,
            CountDate = dto.CountDate,
            Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim(),
            CreatedById = db.User.RequireUserId(),
        };
        foreach (var l in plain.Concat(lots).OrderBy(x => x.Sku, StringComparer.Ordinal).ThenBy(x => x.LotNo, StringComparer.Ordinal))
        {
            count.Lines.Add(new StockCountLine { VariantId = l.VariantId, LotId = l.LotId, ExpectedQty = l.Quantity });
        }
        db.StockCounts.Add(count);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(count.Id);
    }

    public async Task<StockCountDetailDto> UpdateLinesAsync(Guid id, List<CountLineUpdate> lines)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await DraftAsync(id);
        var ids = lines.Select(l => l.Id).ToHashSet();
        var rows = await db.StockCountLines.Where(l => l.CountId == id && ids.Contains(l.Id)).ToListAsync();
        Guard.That(rows.Count == ids.Count, "Bilinmeyen sayım satırı");
        foreach (var l in lines)
        {
            Guard.That(l.CountedQty is null or >= 0, "Sayılan miktar negatif olamaz");
            rows.First(r => r.Id == l.Id).CountedQty = l.CountedQty;
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(id);
    }

    /// <summary>Adds an item that was found on the shelf but is not on the snapshot.</summary>
    public async Task<StockCountDetailDto> AddLineAsync(Guid id, AddCountLineRequest dto)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var count = await DraftAsync(id);
        var v = await db.ProductVariants.Include(x => x.Product).FirstOrDefaultAsync(x => x.Id == dto.VariantId && x.Product.DeletedAt == null);
        Guard.That(v != null && v.Product.Type == ProductType.GOODS, "Ürün bulunamadı");
        Guard.That(v!.Product.TrackingType != TrackingType.SERIAL, "Seri takipli ürünler miktarla sayılmaz");
        var lotNo = dto.LotNo?.Trim();
        Guard.That(v.Product.TrackingType != TrackingType.LOT || !string.IsNullOrEmpty(lotNo), "Lot numarası gerekli");
        Guard.That(v.Product.TrackingType != TrackingType.NONE || string.IsNullOrEmpty(lotNo), "Ürün lot takipli değil");
        Guard.That(dto.CountedQty is null or >= 0, "Sayılan miktar negatif olamaz");

        StockLot? lot = null;
        if (!string.IsNullOrEmpty(lotNo))
        {
            lot = await db.StockLots.FirstOrDefaultAsync(l => l.VariantId == v.Id && l.LotNo == lotNo);
            if (lot == null)
            {
                lot = new StockLot { VariantId = v.Id, LotNo = lotNo };
                db.StockLots.Add(lot);
                await db.SaveChangesAsync();
            }
        }
        var lotId = lot?.Id;
        Guard.State(!await db.StockCountLines.AnyAsync(l => l.CountId == id && l.VariantId == v.Id && l.LotId == lotId), "Ürün zaten sayım listesinde");
        var expected = lot != null
            ? await db.LotBalances.Where(b => b.WarehouseId == count.WarehouseId && b.LotId == lot.Id).Select(b => (decimal?)b.Quantity).FirstOrDefaultAsync()
            : await db.StockBalances.Where(b => b.WarehouseId == count.WarehouseId && b.VariantId == v.Id).Select(b => (decimal?)b.Quantity).FirstOrDefaultAsync();
        db.StockCountLines.Add(new StockCountLine { CountId = id, VariantId = v.Id, LotId = lotId, ExpectedQty = expected ?? 0, CountedQty = dto.CountedQty });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(id);
    }

    /// <summary>Posts the differences of the counted lines as one adjustment and closes the count.</summary>
    public async Task<StockCountDetailDto> CompleteAsync(Guid id)
    {
        List<LowStockItem> lowStock = [];
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var count = await DraftAsync(id);
            var lines = await db.StockCountLines.Where(l => l.CountId == id && l.CountedQty != null).ToListAsync();
            Guard.That(lines.Count > 0, "Henüz hiçbir satır sayılmadı");
            var differences = lines.Select(l => (Line: l, Diff: l.CountedQty!.Value - l.ExpectedQty)).Where(x => x.Diff != 0).ToList();
            if (differences.Count > 0)
            {
                var result = await movements.PostAsync(new CreateMovementRequest(StockMovementType.ADJUSTMENT, count.CountDate, null, count.WarehouseId,
                    count.Number, count.Notes, differences.Select(d => new MovementLineInput(d.Line.VariantId, d.Diff, LotId: d.Line.LotId)).ToList()));
                count.AdjustmentId = result.MovementId;
                lowStock = result.LowStock;
            }
            count.Status = StockCountStatus.COMPLETED;
            count.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        await movements.NotifyLowStockAsync(lowStock);
        return await GetAsync(id);
    }

    public async Task<StockCountDetailDto> CancelAsync(Guid id)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var count = await DraftAsync(id);
        count.Status = StockCountStatus.CANCELLED;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(id);
    }

    public async Task<StockCountDetailDto> GetAsync(Guid id)
    {
        var c = Guard.Found(await db.StockCounts.AsNoTracking().Include(x => x.Warehouse).Include(x => x.CreatedBy).Include(x => x.Adjustment)
            .Include(x => x.Lines).ThenInclude(l => l.Variant).ThenInclude(v => v.Product).ThenInclude(p => p.Unit)
            .Include(x => x.Lines).ThenInclude(l => l.Lot)
            .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id), "Stok sayımı bulunamadı");
        var lines = c.Lines.Select(l => new StockCountLineDto(l.Id, l.VariantId, l.Variant.ProductId, l.Variant.Sku, l.Variant.Barcode, l.Variant.Product.Name,
                l.Variant.Name, l.Variant.Product.Unit.Code, l.LotId, l.Lot?.LotNo, l.ExpectedQty, l.CountedQty, l.CountedQty - l.ExpectedQty))
            .OrderBy(l => l.Sku, StringComparer.Ordinal).ThenBy(l => l.LotNo ?? "", StringComparer.Ordinal).ToList();
        var counted = lines.Where(l => l.CountedQty != null).ToList();
        return new StockCountDetailDto(c.Id, c.Number, c.WarehouseId, c.Warehouse.Name, c.Status, c.CountDate, c.Notes, lines.Count, counted.Count,
            counted.Count(l => l.Difference != 0), $"{c.CreatedBy.FirstName} {c.CreatedBy.LastName}", c.CompletedAt, c.AdjustmentId, c.Adjustment?.Number, lines);
    }

    /// <summary>Loads and locks an open count so concurrent edits and completion cannot interleave.</summary>
    private async Task<StockCount> DraftAsync(Guid id)
    {
        await db.Database.ExecuteSqlAsync($"SELECT id FROM stock_counts WHERE id = {id} FOR UPDATE");
        var count = Guard.Found(await db.StockCounts.FirstOrDefaultAsync(c => c.Id == id), "Stok sayımı bulunamadı");
        Guard.State(count.Status == StockCountStatus.DRAFT, "Yalnızca açık sayımlar değiştirilebilir");
        return count;
    }
}
