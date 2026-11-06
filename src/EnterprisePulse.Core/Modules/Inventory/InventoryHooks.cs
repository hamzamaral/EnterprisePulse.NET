using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Assets;
using EnterprisePulse.Core.Resources;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Inventory;

public sealed class ProductCategoriesHook(AppDbContext db) : IResourceHook
{
    public string Resource => "product-categories";

    public async Task BeforeSaveAsync(object entity, bool isNew)
    {
        var c = (ProductCategory)entity;
        c.Code = c.Code.Trim().ToUpperInvariant();
        if (c.ParentId == null || isNew) return;
        var parents = await db.ProductCategories.Where(x => x.Id != c.Id).Select(x => new { x.Id, x.ParentId }).ToDictionaryAsync(x => x.Id, x => x.ParentId);
        for (Guid? cursor = c.ParentId; cursor != null; cursor = parents.GetValueOrDefault(cursor.Value))
        {
            Guard.That(cursor != c.Id, "Bir kategori kendi alt kategorisinin altına taşınamaz");
        }
    }

    public async Task BeforeDeleteAsync(object entity)
    {
        var c = (ProductCategory)entity;
        Guard.State(!await db.ProductCategories.AnyAsync(x => x.ParentId == c.Id), "Kategorinin alt kategorileri var");
        Guard.State(!await db.Products.IgnoreQueryFilters().AnyAsync(p => p.CategoryId == c.Id), "Kategoride ürün var; silmek yerine pasifleştirin");
    }
}

public sealed class WarehousesHook(AppDbContext db) : IResourceHook
{
    public string Resource => "warehouses";

    public async Task BeforeSaveAsync(object entity, bool isNew)
    {
        var w = (Warehouse)entity;
        w.Code = w.Code.Trim().ToUpperInvariant();
        if (!isNew && !w.IsActive && db.Entry(w).Property(x => x.IsActive).OriginalValue)
        {
            Guard.State(!await db.StockBalances.AnyAsync(b => b.WarehouseId == w.Id && b.Quantity != 0), "Depoda hâlâ stok var; önce transfer edin");
        }
    }

    public async Task BeforeDeleteAsync(object entity)
    {
        var w = (Warehouse)entity;
        var used = await db.StockTransactions.AnyAsync(t => t.WarehouseId == w.Id) || await db.StockCounts.AnyAsync(c => c.WarehouseId == w.Id)
            || await db.PurchaseOrders.AnyAsync(o => o.WarehouseId == w.Id);
        Guard.State(!used, "Depo hareket görmüş; silmek yerine pasifleştirin");
        await db.StockBalances.Where(b => b.WarehouseId == w.Id).ExecuteDeleteAsync();
    }
}

/// <summary>
/// Products always have a default variant (its SKU is the product code). Type and tracking are fixed once stock has
/// moved, and a product with history is deactivated rather than deleted.
/// </summary>
public sealed class ProductsHook(AppDbContext db, InventoryService inventory) : IResourceHook
{
    public string Resource => "products";

    public async Task BeforeSaveAsync(object entity, bool isNew)
    {
        var p = (Product)entity;
        p.Code = p.Code.Trim();
        p.Name = p.Name.Trim();
        Guard.That(p.Type != ProductType.SERVICE || p.TrackingType == TrackingType.NONE, "Hizmetler lot veya seri takipli olamaz");
        Guard.That(p.PurchasePrice is null or >= 0 && p.SalePrice is null or >= 0, "Fiyatlar negatif olamaz");
        if (isNew)
        {
            Guard.State(!await db.ProductVariants.AnyAsync(v => v.Sku == p.Code), $"{p.Code} stok kodu başka bir üründe kullanılıyor");
            return;
        }
        var entry = db.Entry(p);
        var locked = entry.Property(x => x.Type).OriginalValue != p.Type || entry.Property(x => x.TrackingType).OriginalValue != p.TrackingType;
        if (locked)
        {
            var ids = await db.ProductVariants.Where(v => v.ProductId == p.Id).Select(v => v.Id).ToListAsync();
            Guard.State(!await inventory.HasHistoryAsync(ids), "Stok hareketi olan ürünün türü ve takip şekli değiştirilemez");
            Guard.State(p.Type != ProductType.SERVICE || ids.Count <= 1, "Varyantlı ürün hizmete çevrilemez");
        }
    }

    public async Task AfterSaveAsync(object entity, bool isNew)
    {
        var p = (Product)entity;
        if (!isNew || await db.ProductVariants.AnyAsync(v => v.ProductId == p.Id)) return;
        db.ProductVariants.Add(new ProductVariant { ProductId = p.Id, Sku = p.Code, IsDefault = true });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            throw new ConflictAppException($"{p.Code} stok kodu başka bir üründe kullanılıyor");
        }
    }

    /// <summary>Products without stock history are removed with their variants, freeing the SKUs.</summary>
    public async Task BeforeDeleteAsync(object entity)
    {
        var p = (Product)entity;
        var ids = await db.ProductVariants.Where(v => v.ProductId == p.Id).Select(v => v.Id).ToListAsync();
        Guard.State(!await inventory.HasHistoryAsync(ids), "Ürünün stok geçmişi var; silmek yerine pasifleştirin");
        var used = await db.PurchaseRequestLines.AnyAsync(l => l.VariantId != null && ids.Contains(l.VariantId.Value))
            || await db.SupplierQuoteLines.AnyAsync(l => l.VariantId != null && ids.Contains(l.VariantId.Value))
            || await db.PurchaseOrderLines.AnyAsync(l => ids.Contains(l.VariantId));
        Guard.State(!used, "Ürün belgelerde kullanılıyor; silmek yerine pasifleştirin");
        await db.StockBalances.Where(b => ids.Contains(b.VariantId)).ExecuteDeleteAsync();
        await db.ProductVariants.Where(v => v.ProductId == p.Id).ExecuteDeleteAsync();
        p.IsActive = false;
    }
}

public sealed class ProductVariantsHook() : ManagedRecordHook("product-variants", "Varyantlar ürün kartının Varyantlar sekmesinden yönetilir");

public sealed class StockMovementsHook() : ManagedRecordHook("stock-movements", "Stok hareketleri Yeni hareket ekranından işlenir ve sonradan değiştirilemez");

public sealed class StockMovementLinesHook() : ManagedRecordHook("stock-movement-lines", "İşlenmiş hareket satırları değiştirilemez");

public sealed class StockMovementSerialsHook() : ManagedRecordHook("stock-movement-serials", "İşlenmiş hareket satırları değiştirilemez");

public sealed class StockCountsHook() : ManagedRecordHook("stock-counts", "Sayımlar Stok Sayımı ekranından yönetilir");

public sealed class StockCountLinesHook() : ManagedRecordHook("stock-count-lines", "Sayım satırları sayım ekranından güncellenir");

public sealed class StockLotsHook() : ManagedRecordHook("stock-lots", "Lotlar stok girişleriyle oluşur");

public sealed class SerialNumbersHook() : ManagedRecordHook("serial-numbers", "Seri numaraları stok hareketleriyle oluşur");
