using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Accounting;
using EnterprisePulse.Core.Modules.Assets;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Seeding;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Production;

public sealed class BillOfMaterialsHook() : ManagedRecordHook("bill-of-materials", "Ürün ağaçları Üretim > Ürün Ağaçları ekranından kaydedilir");

public sealed class BomLinesHook() : ManagedRecordHook("bom-lines", "Ürün ağacı bileşenleri reçeteyle birlikte kaydedilir");

public sealed class WorkOrdersHook() : ManagedRecordHook("work-orders", "İş emirleri Üretim > İş Emirleri ekranından yönetilir");

public sealed class WorkOrderComponentsHook() : ManagedRecordHook("work-order-components", "İş emri bileşenleri ürün ağacından kopyalanır");

public sealed class ProductionRecordsHook() : ManagedRecordHook("production-records", "Üretim fişleri iş emri üzerinden girilir; stok hareketleri otomatik oluşur");

public sealed class ProductionDashboard(AppDbContext db, ProductionService production) : IDashboardProvider
{
    public int Order => 55;

    public async Task<DashboardSection?> SectionAsync()
    {
        if (!db.User.Has("production:read")) return null;
        var s = await production.SummaryAsync();
        return new DashboardSection("production", "Üretim",
        [
            new("production-open", "Açık iş emri", (s.Draft + s.Released + s.InProgress).ToString(), "PrecisionManufacturing", Link: "/r/work-orders"),
            new("production-late", "Geciken iş emri", s.Late.ToString(), "RunningWithErrors", Link: "/r/work-orders", Color: s.Late > 0 ? "error" : "primary"),
            new("production-shortage", "Malzeme eksiği", s.Shortages.ToString(), "Inventory", Link: "/production/requirements", Color: s.Shortages > 0 ? "warning" : "primary"),
        ]);
    }
}

/// <summary>
/// Production demo data: made products and their BOMs (the starter kit uses the workstation set, so MRP shows two levels) and work orders in
/// every open state. Production records go through the real service, so stock movements and costs are posted as in daily use.
/// </summary>
public sealed class ProductionSeed(AppDbContext db, ProductionService production) : ISeedStep
{
    public int Order => 81;

    private sealed record MadeProduct(string Code, string Name, string Unit, decimal SalePrice, decimal MinStock);

    private sealed record SampleBom(string Sku, string Name, decimal Quantity, decimal Labor, decimal Overhead, string? Notes, (string Sku, decimal Quantity, decimal Scrap)[] Lines);

    private sealed record SampleOrder(string Sku, decimal Quantity, WorkOrderStatus Status, int Start, int End, string? Reference, (int DaysAgo, decimal Quantity)[] Records);

    private static readonly MadeProduct[] Products =
    [
        new("PRD-ISTASYON", "Çalışma İstasyonu Seti", "ADET", 10900, 2),
        new("PRD-KIT", "Yeni Personel Başlangıç Kiti", "ADET", 12900, 0),
        new("PRD-IKRAM", "Toplantı İkram Paketi", "ADET", 790, 0),
    ];

    private static readonly SampleBom[] Boms =
    [
        new("PRD-ISTASYON", "Çalışma istasyonu montajı", 1, 250, 75, "Monitör, klavye ve mouse kutudan çıkarılıp test edilir, set halinde paketlenir.",
            [("PRD-MONITOR", 1, 0), ("PRD-KLAVYE-Q", 1, 0), ("PRD-MOUSE-SYH", 1, 0)]),
        new("PRD-KIT", "Personel başlangıç kiti", 1, 120, 40, null, [("PRD-ISTASYON", 1, 0), ("PRD-POLO-LCV-M", 1, 0), ("PRD-A4", 1, 0)]),
        new("PRD-IKRAM", "İkram paketi (10 kişilik)", 10, 300, 100, null, [("PRD-KAHVE", 5, 4), ("PRD-SU", 20, 0)]),
    ];

    private static readonly SampleOrder[] Orders =
    [
        new("PRD-ISTASYON", 3, WorkOrderStatus.COMPLETED, 14, 12, "Bilgi İşlem talebi #131", [(12, 3)]),
        new("PRD-KIT", 12, WorkOrderStatus.RELEASED, -3, -7, "Eylül işe alım dönemi", []),
        new("PRD-IKRAM", 20, WorkOrderStatus.DRAFT, -10, -11, null, []),
        new("PRD-ISTASYON", 6, WorkOrderStatus.IN_PROGRESS, 6, 1, null, [(2, 2)]),
    ];

    public async Task RunAsync(SeedContext ctx)
    {
        using var _ = db.User.ActAs(ctx.PulseId, ctx.AdminId, superAdmin: true);
        if (await db.BillOfMaterials.AnyAsync()) return;
        await EnsureProductsAsync();

        var skus = Boms.SelectMany(b => b.Lines.Select(l => l.Sku).Append(b.Sku)).Distinct().ToList();
        var variants = await db.ProductVariants.Where(v => skus.Contains(v.Sku)).ToDictionaryAsync(v => v.Sku, v => v.Id);
        if (variants.Count != skus.Count) return;

        var bomIds = new Dictionary<string, Guid>();
        foreach (var b in Boms)
        {
            var created = await production.CreateBomAsync(new CreateBom(variants[b.Sku], $"{b.Sku}-R1", b.Name, b.Quantity, b.Labor, b.Overhead, true, b.Notes,
                b.Lines.Select(l => new BomLineInput(variants[l.Sku], l.Quantity, l.Scrap, null)).ToList()));
            bomIds[b.Sku] = created.Id;
        }

        var warehouse = await db.Warehouses.FirstOrDefaultAsync(w => w.Code == "MRK");
        if (warehouse == null) return;
        var today = AccountingClock.Today;
        DateOnly Day(int daysAgo) => today.AddDays(-daysAgo);

        foreach (var o in Orders)
        {
            var created = await production.CreateWorkOrderAsync(new CreateWorkOrder(bomIds[o.Sku], o.Quantity, Day(o.Start), Day(o.End), warehouse.Id, warehouse.Id, o.Reference, null));
            if (o.Status != WorkOrderStatus.DRAFT) await production.ReleaseAsync(created.Order.Id);
            foreach (var (daysAgo, quantity) in o.Records)
                await production.RecordAsync(created.Order.Id, new RecordProduction(quantity, Day(daysAgo), null, null, null, null, null));

            // Backdate the audit timestamps to match the planned dates.
            var row = await db.WorkOrders.FirstAsync(w => w.Id == created.Order.Id);
            var createdAt = DateTime.SpecifyKind(Day(Math.Max(o.Start, 0) + 2).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            row.CreatedAt = createdAt;
            if (o.Status != WorkOrderStatus.DRAFT) row.ReleasedAt = createdAt;
            if (o.Records.Length > 0)
            {
                var first = DateTime.SpecifyKind(Day(o.Records[0].DaysAgo).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
                row.StartedAt = first;
                row.CompletedAt = o.Status == WorkOrderStatus.COMPLETED ? first : null;
            }
            await db.SaveChangesAsync();
        }
    }

    private async Task EnsureProductsAsync()
    {
        var category = await db.ProductCategories.FirstOrDefaultAsync(c => c.Code == "BLG");
        var vat = await db.TaxRates.Where(t => t.Code == "KDV20").Select(t => (Guid?)t.Id).FirstOrDefaultAsync();
        var units = await db.Units.ToDictionaryAsync(u => u.Code, u => u.Id);
        foreach (var p in Products.Where(p => units.ContainsKey(p.Unit)))
        {
            if (await db.Products.AnyAsync(x => x.Code == p.Code)) continue;
            var product = new Product { Code = p.Code, Name = p.Name, CategoryId = category?.Id, UnitId = units[p.Unit], SalePrice = p.SalePrice, VatRateId = vat };
            product.Variants.Add(new ProductVariant { Sku = p.Code, MinStock = p.MinStock, IsDefault = true });
            db.Products.Add(product);
        }
        await db.SaveChangesAsync();
    }
}
