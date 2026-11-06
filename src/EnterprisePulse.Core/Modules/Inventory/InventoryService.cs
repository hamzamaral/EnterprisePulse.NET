using System.Text.Json;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Inventory;

public sealed record StockLevelQuery(Guid? WarehouseId, Guid? CategoryId, bool LowStock, string? Search, string? Sort, bool Desc, int Page = 1, int PageSize = 25);

public sealed record StockLevelDto(
    Guid VariantId, Guid ProductId, string Sku, string ProductName, string? VariantName, string? CategoryName, string UnitCode,
    TrackingType TrackingType, decimal Quantity, decimal MinStock, decimal AverageCost, decimal StockValue, bool LowStock);

public sealed record StockSummaryDto(int ItemCount, int LowStockCount, decimal StockValue, int WarehouseCount);

public sealed record StockItemDto(
    Guid VariantId, Guid ProductId, string Sku, string ProductName, string? VariantName, string Label, string UnitCode, ProductType ProductType,
    TrackingType TrackingType, decimal AverageCost, decimal OnHand, decimal Reserved, decimal Available, decimal? PurchasePrice,
    decimal? SalePrice, string PriceCurrency, decimal? VatRate);

public sealed record LotStockDto(Guid LotId, string LotNo, DateOnly? ExpiryDate, Guid WarehouseId, string WarehouseName, decimal Quantity);

public sealed record SerialDto(Guid Id, Guid VariantId, string Sku, string SerialNo, SerialStatus Status, Guid? WarehouseId, string? WarehouseName);

public sealed record LedgerEntryDto(
    Guid Id, Guid MovementId, string MovementNumber, StockMovementType MovementType, DateOnly MovementDate, Guid WarehouseId,
    string WarehouseName, string Sku, string? LotNo, decimal Quantity, decimal UnitCost, decimal BalanceAfter, DateTime CreatedAt);

public sealed record VariantDto(
    Guid Id, string Sku, string? Name, Dictionary<string, string>? Attributes, string? Barcode, decimal MinStock, decimal AverageCost,
    bool IsDefault, bool IsActive, decimal OnHand, bool LowStock, bool HasHistory);

public sealed record WarehouseStockDto(Guid WarehouseId, string WarehouseName, Guid VariantId, string Sku, decimal Quantity);

public sealed record ProductStockDto(
    Guid Id, string Code, string Name, ProductType Type, TrackingType TrackingType, string UnitCode, decimal OnHand, decimal StockValue,
    bool LowStock, bool HasHistory, List<VariantDto> Variants, List<WarehouseStockDto> Stock);

public sealed record VariantInput(string? Sku, string? Name, Dictionary<string, string>? Attributes, string? Barcode, decimal? MinStock, bool? IsActive);

public sealed record WarehouseSummaryDto(Guid Id, int ItemCount, decimal StockValue);

/// <summary>Read models over balances and the ledger, plus product variants (Stok ve Depo).</summary>
[ModuleService]
public sealed class InventoryService(AppDbContext db)
{
    // ---------- stock levels ----------

    /// <summary>
    /// One row per stockable variant. With a warehouse the quantity is that warehouse's balance (items present there);
    /// otherwise the company total. Low stock always compares the company total with the reorder point.
    /// </summary>
    public async Task<Paged<StockLevelDto>> LevelsAsync(StockLevelQuery query)
    {
        var wh = query.WarehouseId;
        var rows = db.ProductVariants.AsNoTracking()
            .Where(v => v.Product.DeletedAt == null && v.Product.Type == ProductType.GOODS)
            .Select(v => new
            {
                v,
                Total = v.Balances.Sum(b => (decimal?)b.Quantity) ?? 0,
                Here = wh == null ? 0 : v.Balances.Where(b => b.WarehouseId == wh).Sum(b => (decimal?)b.Quantity) ?? 0,
            })
            .Select(x => new
            {
                x.v, x.Total, x.Here,
                Quantity = wh == null ? x.Total : x.Here,
            });
        rows = wh != null ? rows.Where(x => x.Here != 0) : rows.Where(x => (x.v.IsActive && x.v.Product.IsActive) || x.Total != 0);
        if (query.CategoryId != null) rows = rows.Where(x => x.v.Product.CategoryId == query.CategoryId);
        if (query.LowStock) rows = rows.Where(x => x.v.MinStock > 0 && x.Total < x.v.MinStock);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim();
            var like = $"%{s}%";
            rows = rows.Where(x => EF.Functions.ILike(x.v.Sku, like) || EF.Functions.ILike(x.v.Product.Code, like) || EF.Functions.ILike(x.v.Product.Name, like)
                || (x.v.Name != null && EF.Functions.ILike(x.v.Name, like)) || x.v.Barcode == s);
        }
        var total = await rows.CountAsync();
        var ordered = (query.Sort, query.Desc) switch
        {
            ("productName", false) => rows.OrderBy(x => x.v.Product.Name).ThenBy(x => x.v.Sku),
            ("productName", true) => rows.OrderByDescending(x => x.v.Product.Name).ThenBy(x => x.v.Sku),
            ("quantity", false) => rows.OrderBy(x => x.Quantity).ThenBy(x => x.v.Sku),
            ("quantity", true) => rows.OrderByDescending(x => x.Quantity).ThenBy(x => x.v.Sku),
            ("stockValue", false) => rows.OrderBy(x => x.Quantity * x.v.AverageCost).ThenBy(x => x.v.Sku),
            ("stockValue", true) => rows.OrderByDescending(x => x.Quantity * x.v.AverageCost).ThenBy(x => x.v.Sku),
            ("sku", true) => rows.OrderByDescending(x => x.v.Sku),
            _ => rows.OrderBy(x => x.v.Sku),
        };
        var size = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);
        var data = await ordered.Skip((page - 1) * size).Take(size).Select(x => new StockLevelDto(
            x.v.Id, x.v.ProductId, x.v.Sku, x.v.Product.Name, x.v.Name, x.v.Product.Category != null ? x.v.Product.Category.Name : null,
            x.v.Product.Unit.Code, x.v.Product.TrackingType, x.Quantity, x.v.MinStock, x.v.AverageCost,
            Math.Round(x.Quantity * x.v.AverageCost, 2), x.v.MinStock > 0 && x.Total < x.v.MinStock)).ToListAsync();
        return Paged<StockLevelDto>.Of(data, total, page, size);
    }

    public async Task<StockSummaryDto> SummaryAsync()
    {
        var rows = await db.ProductVariants.AsNoTracking()
            .Where(v => v.Product.DeletedAt == null && v.Product.Type == ProductType.GOODS && v.IsActive && v.Product.IsActive)
            .Select(v => new { v.MinStock, v.AverageCost, Total = v.Balances.Sum(b => (decimal?)b.Quantity) ?? 0 }).ToListAsync();
        var warehouses = await db.Warehouses.CountAsync(w => w.IsActive);
        return new StockSummaryDto(rows.Count(r => r.Total > 0), rows.Count(r => r.MinStock > 0 && r.Total < r.MinStock),
            Math.Round(rows.Sum(r => r.Total * r.AverageCost), 2), warehouses);
    }

    // ---------- pickers and detail lists ----------

    /// <summary>Variants for item pickers: max. 20 active goods (services on request).</summary>
    public async Task<List<StockItemDto>> ItemsAsync(string? search, Guid? warehouseId, bool inStock, bool includeServices, Guid? id = null)
    {
        var q = db.ProductVariants.AsNoTracking().Where(v => v.IsActive && v.Product.DeletedAt == null && v.Product.IsActive);
        if (id != null) q = db.ProductVariants.AsNoTracking().Where(v => v.Id == id);
        if (!includeServices && id == null) q = q.Where(v => v.Product.Type == ProductType.GOODS);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            var like = $"%{s}%";
            q = q.Where(v => EF.Functions.ILike(v.Sku, like) || (v.Name != null && EF.Functions.ILike(v.Name, like)) || v.Barcode == s
                || EF.Functions.ILike(v.Product.Name, like));
        }
        if (inStock && warehouseId != null) q = q.Where(v => v.Balances.Any(b => b.WarehouseId == warehouseId && b.Quantity > 0));
        var rows = await q.OrderBy(v => v.Sku).Take(20).Select(v => new
        {
            v.Id, v.ProductId, v.Sku, ProductName = v.Product.Name, v.Name, Unit = v.Product.Unit.Code, v.Product.Type, v.Product.TrackingType,
            v.AverageCost, v.Product.PurchasePrice, v.Product.SalePrice, v.Product.Currency, Vat = v.Product.VatRate != null ? (decimal?)v.Product.VatRate.Rate : null,
            OnHand = v.Balances.Where(b => warehouseId == null || b.WarehouseId == warehouseId).Sum(b => (decimal?)b.Quantity) ?? 0,
            Reserved = v.Reservations.Where(r => warehouseId == null || r.WarehouseId == warehouseId).Sum(r => (decimal?)r.Quantity) ?? 0,
        }).ToListAsync();
        return rows.Select(r => new StockItemDto(r.Id, r.ProductId, r.Sku, r.ProductName, r.Name, r.Name is { Length: > 0 } n ? $"{r.ProductName} ({n})" : r.ProductName,
            r.Unit, r.Type, r.TrackingType, r.AverageCost, r.OnHand, r.Reserved, r.OnHand - r.Reserved, r.PurchasePrice, r.SalePrice, r.Currency, r.Vat)).ToList();
    }

    /// <summary>Lots that still have stock (per warehouse), oldest expiry first (FEFO).</summary>
    public async Task<List<LotStockDto>> LotsAsync(Guid? variantId, Guid? warehouseId)
    {
        var rows = await db.LotBalances.AsNoTracking()
            .Where(b => b.Quantity > 0 && (variantId == null || b.Lot.VariantId == variantId) && (warehouseId == null || b.WarehouseId == warehouseId))
            .Select(b => new LotStockDto(b.LotId, b.Lot.LotNo, b.Lot.ExpiryDate, b.WarehouseId, b.Warehouse.Name, b.Quantity)).ToListAsync();
        return rows.OrderBy(r => r.ExpiryDate ?? DateOnly.MaxValue).ThenBy(r => r.LotNo, StringComparer.Ordinal).ToList();
    }

    public Task<List<SerialDto>> SerialsAsync(Guid? variantId, Guid? warehouseId, SerialStatus? status) =>
        db.SerialNumbers.AsNoTracking()
            .Where(s => (variantId == null || s.VariantId == variantId) && (warehouseId == null || s.WarehouseId == warehouseId) && (status == null || s.Status == status))
            .OrderBy(s => s.SerialNo).Take(1000)
            .Select(s => new SerialDto(s.Id, s.VariantId, s.Variant.Sku, s.SerialNo, s.Status, s.WarehouseId, s.Warehouse != null ? s.Warehouse.Name : null))
            .ToListAsync();

    /// <summary>Stock card: ledger entries, newest first.</summary>
    public async Task<Paged<LedgerEntryDto>> LedgerAsync(Guid? productId, Guid? variantId, Guid? warehouseId, int page, int pageSize)
    {
        var q = db.StockTransactions.AsNoTracking()
            .Where(t => (variantId == null || t.VariantId == variantId) && (productId == null || t.Variant.ProductId == productId)
                && (warehouseId == null || t.WarehouseId == warehouseId));
        var total = await q.CountAsync();
        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);
        var rows = await q.OrderByDescending(t => t.Seq).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(t => new LedgerEntryDto(t.Id, t.MovementId, t.Movement.Number, t.Movement.Type, t.MovementDate, t.WarehouseId, t.Warehouse.Name,
                t.Variant.Sku, t.Lot != null ? t.Lot.LotNo : null, t.Quantity, t.UnitCost, t.BalanceAfter, t.CreatedAt)).ToListAsync();
        return Paged<LedgerEntryDto>.Of(rows, total, page, pageSize);
    }

    public async Task<List<WarehouseSummaryDto>> WarehousesAsync()
    {
        var ids = await db.Warehouses.Select(w => w.Id).ToListAsync();
        return await WarehouseSummaryAsync(ids);
    }

    /// <summary>Item count (balances above zero) and stock value of each warehouse.</summary>
    public async Task<List<WarehouseSummaryDto>> WarehouseSummaryAsync(IReadOnlyCollection<Guid> ids)
    {
        var rows = await db.StockBalances.AsNoTracking().Where(b => ids.Contains(b.WarehouseId) && b.Quantity > 0).GroupBy(b => b.WarehouseId)
            .Select(g => new WarehouseSummaryDto(g.Key, g.Count(), g.Sum(b => b.Quantity * b.Variant.AverageCost))).ToListAsync();
        return ids.Select(id => rows.FirstOrDefault(r => r.Id == id) is { } r ? r with { StockValue = Math.Round(r.StockValue, 2) } : new WarehouseSummaryDto(id, 0, 0)).ToList();
    }

    /// <summary>On-hand total and low-stock flag per product, for the product list.</summary>
    public async Task<Dictionary<Guid, (decimal OnHand, bool Low)>> ProductStockAsync(IReadOnlyCollection<Guid> productIds)
    {
        var rows = await db.ProductVariants.AsNoTracking().Where(v => productIds.Contains(v.ProductId))
            .Select(v => new { v.ProductId, v.IsActive, v.MinStock, OnHand = v.Balances.Sum(b => (decimal?)b.Quantity) ?? 0 }).ToListAsync();
        return rows.GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => (g.Sum(r => r.OnHand), g.Any(r => r.IsActive && r.MinStock > 0 && r.OnHand < r.MinStock)));
    }

    // ---------- product stock and variants ----------

    public async Task<ProductStockDto> ProductAsync(Guid productId)
    {
        var p = Guard.Found(await db.Products.AsNoTracking().Include(x => x.Unit).Include(x => x.Variants).FirstOrDefaultAsync(x => x.Id == productId), "Ürün bulunamadı");
        var ids = p.Variants.Select(v => v.Id).ToList();
        var onHand = await db.StockBalances.Where(b => ids.Contains(b.VariantId)).GroupBy(b => b.VariantId)
            .Select(g => new { g.Key, Total = g.Sum(b => b.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Total);
        var history = (await db.StockTransactions.Where(t => ids.Contains(t.VariantId)).Select(t => t.VariantId).Distinct().ToListAsync())
            .Concat(await db.StockCountLines.Where(l => ids.Contains(l.VariantId)).Select(l => l.VariantId).Distinct().ToListAsync()).ToHashSet();
        var stock = await db.StockBalances.AsNoTracking().Where(b => ids.Contains(b.VariantId) && b.Quantity != 0).OrderBy(b => b.Warehouse.Name).ThenBy(b => b.Variant.Sku)
            .Select(b => new WarehouseStockDto(b.WarehouseId, b.Warehouse.Name, b.VariantId, b.Variant.Sku, b.Quantity)).ToListAsync();
        var variants = p.Variants.OrderByDescending(v => v.IsDefault).ThenBy(v => v.Sku).Select(v =>
        {
            var q = onHand.GetValueOrDefault(v.Id);
            return new VariantDto(v.Id, v.Sku, v.Name, v.Attributes?.Deserialize<Dictionary<string, string>>(), v.Barcode, v.MinStock, v.AverageCost,
                v.IsDefault, v.IsActive, q, v.IsActive && v.MinStock > 0 && q < v.MinStock, history.Contains(v.Id));
        }).ToList();
        return new ProductStockDto(p.Id, p.Code, p.Name, p.Type, p.TrackingType, p.Unit.Code, variants.Sum(v => v.OnHand),
            Math.Round(variants.Sum(v => v.OnHand * v.AverageCost), 2), variants.Any(v => v.LowStock), history.Count > 0, variants, stock);
    }

    public async Task<ProductStockDto> AddVariantAsync(Guid productId, VariantInput input)
    {
        var p = Guard.Found(await db.Products.FirstOrDefaultAsync(x => x.Id == productId), "Ürün bulunamadı");
        Guard.That(p.Type != ProductType.SERVICE, "Hizmetlerin varyantı olmaz");
        Guard.That(!string.IsNullOrWhiteSpace(input.Sku), "Stok kodu (SKU) gerekli");
        var variant = new ProductVariant { ProductId = p.Id, Sku = input.Sku!.Trim() };
        db.ProductVariants.Add(variant);
        Apply(variant, input);
        await SaveAsync();
        return await ProductAsync(productId);
    }

    public async Task<ProductStockDto> UpdateVariantAsync(Guid productId, Guid variantId, VariantInput input)
    {
        var v = Guard.Found(await db.ProductVariants.FirstOrDefaultAsync(x => x.Id == variantId && x.ProductId == productId), "Varyant bulunamadı");
        if (input.Sku is { } sku && sku.Trim() != v.Sku)
        {
            Guard.State(!await HasHistoryAsync([variantId]), "Stok hareketi olan varyantın stok kodu değiştirilemez");
            v.Sku = sku.Trim();
        }
        Apply(v, input);
        await SaveAsync();
        return await ProductAsync(productId);
    }

    public async Task<ProductStockDto> RemoveVariantAsync(Guid productId, Guid variantId)
    {
        var variants = await db.ProductVariants.Where(x => x.ProductId == productId).ToListAsync();
        var v = Guard.Found(variants.FirstOrDefault(x => x.Id == variantId), "Varyant bulunamadı");
        Guard.State(variants.Count > 1, "Bir ürünün en az bir varyantı olmalı");
        Guard.State(!await HasHistoryAsync([variantId]), "Varyantın stok geçmişi var; silmek yerine pasifleştirin");
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.StockBalances.Where(b => b.VariantId == variantId).ExecuteDeleteAsync();
        db.ProductVariants.Remove(v);
        if (v.IsDefault) variants.First(x => x.Id != variantId).IsDefault = true;
        await SaveAsync();
        await tx.CommitAsync();
        return await ProductAsync(productId);
    }

    public async Task<bool> HasHistoryAsync(IReadOnlyCollection<Guid> variantIds) =>
        await db.StockTransactions.AnyAsync(t => variantIds.Contains(t.VariantId)) || await db.StockCountLines.AnyAsync(l => variantIds.Contains(l.VariantId));

    private static void Apply(ProductVariant v, VariantInput input)
    {
        if (input.Name != null) v.Name = input.Name.Trim() is { Length: > 0 } n ? n : null;
        if (input.Barcode != null) v.Barcode = input.Barcode.Trim() is { Length: > 0 } b ? b : null;
        if (input.MinStock != null)
        {
            Guard.That(input.MinStock >= 0, "Minimum stok negatif olamaz");
            v.MinStock = input.MinStock.Value;
        }
        if (input.Attributes != null)
        {
            var clean = input.Attributes.Where(kv => kv.Key.Trim().Length > 0).ToDictionary(kv => kv.Key.Trim(), kv => kv.Value.Trim());
            v.Attributes = clean.Count == 0 ? null : JsonSerializer.SerializeToDocument(clean);
        }
        if (input.IsActive != null) v.IsActive = input.IsActive.Value;
    }

    private async Task SaveAsync()
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new ConflictAppException("Bu stok kodu veya barkod zaten kullanılıyor");
        }
    }
}
