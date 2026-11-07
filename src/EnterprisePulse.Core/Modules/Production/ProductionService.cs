using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Accounting;
using EnterprisePulse.Core.Modules.Inventory;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Purchasing;
using EnterprisePulse.Core.Modules.Trade;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Production;

public sealed record VariantInfo(Guid VariantId, Guid ProductId, string Sku, string ProductName, string? VariantName, string UnitCode);

public sealed record BomLineInput(Guid VariantId, decimal Quantity, decimal? ScrapRate, string? Notes);

public sealed record CreateBom(Guid VariantId, string? Code, string Name, decimal? Quantity, decimal? LaborCost, decimal? OverheadCost, bool? IsDefault, string? Notes, List<BomLineInput> Lines);

public sealed record UpdateBom(string? Code, string? Name, decimal? Quantity, decimal? LaborCost, decimal? OverheadCost, bool? IsDefault, bool? IsActive, string? Notes, List<BomLineInput>? Lines);

public sealed record BomPlanLineDto(
    int Level, Guid VariantId, Guid ParentId, string Sku, string Name, string UnitCode, decimal Required, decimal OnHand, decimal Shortage, bool Made, string Availability);

public sealed record BomPlanDto(decimal Quantity, decimal MaterialCost, decimal LaborCost, decimal OverheadCost, decimal TotalCost, List<BomPlanLineDto> Lines);

public sealed record BomLineDto(
    Guid Id, int LineNo, VariantInfo Variant, decimal Quantity, decimal ScrapRate, decimal QuantityPer, decimal UnitCost, decimal LineCost, decimal OnHand, bool Made, Guid? MadeBomId, string? Notes);

public sealed record BomDetailDto(
    Guid Id, string Code, string Name, VariantInfo Variant, decimal Quantity, decimal LaborCost, decimal OverheadCost, bool IsDefault, bool IsActive, int LineCount,
    decimal UnitCost, int WorkOrderCount, DateTime UpdatedAt, string? Notes, decimal MaterialCost, string CreatedByName, DateTime CreatedAt, List<BomLineDto> Lines,
    decimal Buildable, bool CanDelete);

public sealed record CreateWorkOrder(Guid BomId, decimal Quantity, DateOnly PlannedStart, DateOnly PlannedEnd, Guid SourceWarehouseId, Guid TargetWarehouseId, string? Reference, string? Notes);

public sealed record UpdateWorkOrder(decimal? Quantity, DateOnly? PlannedStart, DateOnly? PlannedEnd, Guid? SourceWarehouseId, Guid? TargetWarehouseId, string? Reference, string? Notes);

public sealed record ComponentUse(Guid VariantId, decimal Quantity);

public sealed record RecordProduction(decimal Quantity, DateOnly? RecordDate, string? LotNo, DateOnly? ExpiryDate, bool? Finish, string? Notes, List<ComponentUse>? Components);

public sealed record WorkOrderDto(
    Guid Id, string Number, WorkOrderStatus Status, Guid BomId, string BomCode, VariantInfo Variant, decimal Quantity, decimal ProducedQty, int Progress,
    DateOnly PlannedStart, DateOnly PlannedEnd, bool Late, Guid SourceWarehouseId, string SourceWarehouseName, Guid TargetWarehouseId, string TargetWarehouseName,
    string? Reference, DateTime CreatedAt);

public sealed record WorkOrderComponentDto(
    Guid Id, int LineNo, VariantInfo Variant, decimal QuantityPer, decimal RequiredQty, decimal ConsumedQty, decimal RemainingQty, decimal OnHand, string Availability, bool LotTracked);

public sealed record ProductionRecordDto(
    Guid Id, string Number, DateOnly RecordDate, decimal Quantity, decimal MaterialCost, decimal LaborCost, decimal OverheadCost, decimal TotalCost, decimal UnitCost,
    Guid? IssueMovementId, string? IssueMovementNumber, Guid ReceiptMovementId, string ReceiptMovementNumber, string? LotNo, string? Notes, string CreatedByName, DateTime CreatedAt);

public sealed record WorkOrderDetailDto(
    WorkOrderDto Order, string? Notes, decimal LaborPerUnit, decimal OverheadPerUnit, decimal StandardUnitCost, decimal? ActualUnitCost, bool LotTracked, string CreatedByName,
    DateTime? ReleasedAt, DateTime? StartedAt, DateTime? CompletedAt, DateTime? CancelledAt, List<WorkOrderComponentDto> Components, List<ProductionRecordDto> Records,
    decimal Buildable, bool CanEdit, bool CanRelease, bool CanRecord, bool CanCancel, bool CanComplete);

public sealed record RequirementOrderDto(Guid Id, string Number, decimal Quantity, DateOnly Date);

public sealed record RequirementDto(
    VariantInfo Variant, decimal ProductionDemand, decimal SalesDemand, decimal Gross, decimal OnHand, decimal PurchaseIncoming, decimal ProductionIncoming, decimal Shortage,
    DateOnly? NeededBy, bool Made, Guid? DefaultBomId, decimal? PurchasePrice, List<RequirementOrderDto> WorkOrders, List<RequirementOrderDto> SalesOrders);

public sealed record RequirementLineInput(Guid VariantId, decimal Quantity);

public sealed record CreateRequirementRequest(DateOnly? NeededBy, List<RequirementLineInput> Lines);

public sealed record CreatedDocumentDto(Guid Id, string Number);

public sealed record ProductionSummaryDto(
    int ActiveBoms, int Draft, int Released, int InProgress, int Late, int CompletedThisMonth, decimal ProducedValueThisMonth, int Shortages, List<WorkOrderDto> Upcoming);

/// <summary>
/// Unit costs for planning: the moving average cost, else the rolled-up cost of the default BOM, else the purchase price.
/// </summary>
public sealed class CostBook(Dictionary<Guid, (decimal AverageCost, decimal? PurchasePrice)> variants, Dictionary<Guid, BomShape> boms)
{
    private readonly Dictionary<Guid, decimal> _memo = [];

    public Dictionary<Guid, BomShape> Boms => boms;

    public decimal Cost(Guid variantId) => Cost(variantId, []);

    private decimal Cost(Guid variantId, HashSet<Guid> trail)
    {
        if (_memo.TryGetValue(variantId, out var known)) return known;
        var value = variants.TryGetValue(variantId, out var v) ? v.AverageCost : 0m;
        if (value == 0 && boms.TryGetValue(variantId, out var bom) && !trail.Contains(variantId))
        {
            trail.Add(variantId);
            value = ProductionMath.RollUpCost(bom, id => Cost(id, trail)).UnitCost;
            trail.Remove(variantId);
        }
        if (value == 0) value = v.PurchasePrice ?? 0m;
        _memo[variantId] = value;
        return value;
    }
}

/// <summary>
/// Üretim: bills of materials (reçeteler), work orders (iş emirleri) and the production dashboard / MRP. The material list is copied from
/// the BOM when the order is planned; production records issue the materials from the source warehouse and receive the product into the
/// target warehouse at material + labour + overhead cost. Work orders and MRP live in partial files.
/// </summary>
[ModuleService]
public sealed partial class ProductionService(AppDbContext db, NumberingService numbering, StockMovementService stock, PurchaseRequestService requests)
{
    public const string RecordSequence = "PRODUCTION";

    private CurrentUser Ctx => db.User;

    // ---------- Shared helpers ----------

    private static VariantInfo Info(ProductVariant v) => new(v.Id, v.ProductId, v.Sku, v.Product.Name, v.Name, v.Product.Unit.Code);

    private static string Label(ProductVariant v) => string.IsNullOrEmpty(v.Name) ? v.Product.Name : $"{v.Product.Name} ({v.Name})";

    private static string? TrimOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<Dictionary<Guid, ProductVariant>> LoadVariantsAsync(IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        return await db.ProductVariants.AsNoTracking().Include(v => v.Product).ThenInclude(p => p.Unit).Where(v => list.Contains(v.Id)).ToDictionaryAsync(v => v.Id);
    }

    /// <summary>On-hand quantity per variant, in one warehouse or in all of them.</summary>
    private async Task<Dictionary<Guid, decimal>> OnHandAsync(IEnumerable<Guid> variantIds, Guid? warehouseId = null)
    {
        var ids = variantIds.Distinct().ToList();
        var rows = await db.StockBalances.AsNoTracking().Where(b => ids.Contains(b.VariantId) && (warehouseId == null || b.WarehouseId == warehouseId))
            .GroupBy(b => b.VariantId).Select(g => new { g.Key, Total = g.Sum(b => b.Quantity) }).ToListAsync();
        var result = ids.ToDictionary(id => id, _ => 0m);
        foreach (var r in rows) result[r.Key] = r.Total;
        return result;
    }

    private static BomShape ShapeOf(BillOfMaterials row) =>
        new(row.Quantity, row.LaborCost, row.OverheadCost, row.Lines.OrderBy(l => l.LineNo).Select(l => new BomLineShape(l.VariantId, l.Quantity, l.ScrapRate)).ToList())
        { Id = row.Id, VariantId = row.VariantId };

    /// <summary>The active default BOM of every variant that has one.</summary>
    private async Task<Dictionary<Guid, BomShape>> DefaultBomsAsync()
    {
        var rows = await db.BillOfMaterials.AsNoTracking().Include(b => b.Lines).Where(b => b.IsDefault && b.IsActive).ToListAsync();
        return rows.ToDictionary(r => r.VariantId, ShapeOf);
    }

    private async Task<CostBook> LoadCostBookAsync(IEnumerable<Guid>? extra = null)
    {
        var boms = await DefaultBomsAsync();
        var ids = new HashSet<Guid>(extra ?? []);
        foreach (var bom in boms.Values)
        {
            ids.Add(bom.VariantId);
            foreach (var line in bom.Lines) ids.Add(line.VariantId);
        }
        var list = ids.ToList();
        var rows = await db.ProductVariants.AsNoTracking().Where(v => list.Contains(v.Id)).Select(v => new { v.Id, v.AverageCost, v.Product.PurchasePrice }).ToListAsync();
        return new CostBook(rows.ToDictionary(r => r.Id, r => (r.AverageCost, r.PurchasePrice)), boms);
    }

    private static void AssertProducible(ProductVariant variant, string label)
    {
        Guard.That(variant.Product.Type == ProductType.GOODS, $"{label}: hizmetler üretilemez veya sarf edilemez");
        Guard.That(variant.Product.TrackingType != TrackingType.SERIAL, $"{label}: seri takipli ürünler üretimde desteklenmez");
        Guard.That(variant.IsActive && variant.Product.IsActive && variant.Product.DeletedAt == null, $"{label}: ürün pasif");
    }

    // ---------- Bills of materials ----------

    public async Task<BomDetailDto> GetBomAsync(Guid id)
    {
        var row = await db.BillOfMaterials.AsNoTracking().AsSplitQuery().Include(b => b.CreatedBy).Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == id)
            ?? throw new NotFoundAppException("Ürün ağacı bulunamadı");
        var componentIds = row.Lines.Select(l => l.VariantId).ToList();
        var book = await LoadCostBookAsync(componentIds);
        var onHand = await OnHandAsync(componentIds);
        var variants = await LoadVariantsAsync(componentIds.Append(row.VariantId));
        var cost = ProductionMath.RollUpCost(ShapeOf(row), book.Cost);
        var workOrders = await db.WorkOrders.CountAsync(w => w.BomId == id);
        var lines = row.Lines.OrderBy(l => l.LineNo).Select(line =>
        {
            var per = ProductionMath.PerUnit(line.Quantity, line.ScrapRate, row.Quantity);
            var unit = book.Cost(line.VariantId);
            book.Boms.TryGetValue(line.VariantId, out var made);
            return new BomLineDto(line.Id, line.LineNo, Info(variants[line.VariantId]), line.Quantity, line.ScrapRate, per, unit,
                DocumentMath.Round2(line.Quantity * (1 + line.ScrapRate / 100m) * unit), onHand.GetValueOrDefault(line.VariantId), made != null, made?.Id, line.Notes);
        }).ToList();
        var buildable = ProductionMath.Buildable(lines.Select(l => new BuildLine(l.QuantityPer, onHand.GetValueOrDefault(l.Variant.VariantId))));
        return new BomDetailDto(row.Id, row.Code, row.Name, Info(variants[row.VariantId]), row.Quantity, row.LaborCost, row.OverheadCost, row.IsDefault, row.IsActive, row.Lines.Count,
            cost.UnitCost, workOrders, row.UpdatedAt, row.Notes, cost.Material, $"{row.CreatedBy.FirstName} {row.CreatedBy.LastName}", row.CreatedAt, lines, buildable, workOrders == 0);
    }

    /// <summary>Net multi-level material plan for making <paramref name="quantity"/> with this BOM.</summary>
    public async Task<BomPlanDto> PlanAsync(Guid id, decimal quantity, Guid? warehouseId)
    {
        Guard.That(quantity > 0, "Miktar sıfırdan büyük olmalı");
        var row = await db.BillOfMaterials.AsNoTracking().Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == id) ?? throw new NotFoundAppException("Ürün ağacı bulunamadı");
        if (warehouseId != null) Guard.That(await db.Warehouses.AnyAsync(w => w.Id == warehouseId), "Depo bulunamadı");
        var book = await LoadCostBookAsync(row.Lines.Select(l => l.VariantId));
        var root = ShapeOf(row);
        BomShape? BomOf(Guid variantId) => variantId == row.VariantId ? root : book.Boms.GetValueOrDefault(variantId);

        var ids = new HashSet<Guid>();
        void Collect(Guid variantId, HashSet<Guid> trail)
        {
            var bom = BomOf(variantId);
            if (bom == null || trail.Contains(variantId)) return;
            trail.Add(variantId);
            foreach (var line in bom.Lines)
            {
                ids.Add(line.VariantId);
                Collect(line.VariantId, trail);
            }
            trail.Remove(variantId);
        }
        Collect(row.VariantId, []);
        var stockMap = await OnHandAsync(ids, warehouseId);
        var variants = await LoadVariantsAsync(ids);
        var lines = ProductionMath.Explode(row.VariantId, quantity, BomOf, stockMap);

        var batches = quantity / row.Quantity;
        var material = ProductionMath.RollUpCost(root, book.Cost).Material * batches;
        var labor = row.LaborCost * batches;
        var overhead = row.OverheadCost * batches;
        return new BomPlanDto(quantity, DocumentMath.Round2(material), DocumentMath.Round2(labor), DocumentMath.Round2(overhead), DocumentMath.Round2(material + labor + overhead),
            lines.Select(l => new BomPlanLineDto(l.Level, l.VariantId, l.ParentId, variants[l.VariantId].Sku, Label(variants[l.VariantId]), variants[l.VariantId].Product.Unit.Code, l.Quantity,
                stockMap.GetValueOrDefault(l.VariantId), l.Shortage, l.Made, ProductionMath.Availability(l.Quantity, l.Allocated))).ToList());
    }

    public async Task<BomDetailDto> CreateBomAsync(CreateBom dto)
    {
        var companyId = Ctx.RequireCompanyId();
        Guard.That(!string.IsNullOrWhiteSpace(dto.Name), "Reçete adı gerekli");
        Guard.That(dto.Lines is { Count: > 0 }, "En az bir bileşen gerekli");
        Guard.That((dto.Quantity ?? 1) > 0, "Parti miktarı sıfırdan büyük olmalı");
        Guid id;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var output = await LoadOutputAsync(dto.VariantId);
            await ValidateLinesAsync(output.Id, dto.Lines, null);
            var code = TrimOrNull(dto.Code)?.ToUpperInvariant() ?? await NextCodeAsync(output);
            await AssertCodeFreeAsync(code, null);
            var hasDefault = await db.BillOfMaterials.AnyAsync(b => b.VariantId == output.Id && b.IsDefault && b.IsActive);
            var isDefault = !hasDefault || dto.IsDefault == true;
            if (isDefault) await ClearDefaultAsync(output.Id);
            var bom = new BillOfMaterials
            {
                CompanyId = companyId, Code = code, Name = dto.Name.Trim(), VariantId = output.Id, Quantity = dto.Quantity ?? 1, LaborCost = dto.LaborCost ?? 0,
                OverheadCost = dto.OverheadCost ?? 0, IsDefault = isDefault, Notes = TrimOrNull(dto.Notes), CreatedById = Ctx.RequireUserId(),
            };
            bom.Lines.AddRange(LineData(dto.Lines, bom.Id));
            db.BillOfMaterials.Add(bom);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            id = bom.Id;
        }
        return await GetBomAsync(id);
    }

    public async Task<BomDetailDto> UpdateBomAsync(Guid id, UpdateBom dto)
    {
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var bom = await db.BillOfMaterials.Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == id) ?? throw new NotFoundAppException("Ürün ağacı bulunamadı");
            if (dto.Lines != null)
            {
                Guard.That(dto.Lines.Count > 0, "En az bir bileşen gerekli");
                await ValidateLinesAsync(bom.VariantId, dto.Lines, bom.Id);
            }
            var code = TrimOrNull(dto.Code)?.ToUpperInvariant();
            if (code != null && code != bom.Code) await AssertCodeFreeAsync(code, bom.Id);
            Guard.That(dto.Quantity == null || dto.Quantity > 0, "Parti miktarı sıfırdan büyük olmalı");

            var wasDefault = bom.IsDefault;
            var isActive = dto.IsActive ?? bom.IsActive;
            Guard.That(!(dto.IsDefault == true && !isActive), "Pasif ürün ağacı varsayılan olamaz");
            var isDefault = isActive && (dto.IsDefault ?? bom.IsDefault);
            if (isDefault && !bom.IsDefault) await ClearDefaultAsync(bom.VariantId);

            if (code != null) bom.Code = code;
            if (!string.IsNullOrWhiteSpace(dto.Name)) bom.Name = dto.Name.Trim();
            if (dto.Quantity != null) bom.Quantity = dto.Quantity.Value;
            if (dto.LaborCost != null) bom.LaborCost = dto.LaborCost.Value;
            if (dto.OverheadCost != null) bom.OverheadCost = dto.OverheadCost.Value;
            if (dto.Notes != null) bom.Notes = TrimOrNull(dto.Notes);
            bom.IsActive = isActive;
            bom.IsDefault = isDefault;
            if (dto.Lines != null)
            {
                db.BomLines.RemoveRange(bom.Lines);
                bom.Lines.Clear();
                await db.SaveChangesAsync();
                bom.Lines.AddRange(LineData(dto.Lines, bom.Id));
            }
            await db.SaveChangesAsync();
            if (wasDefault && !isDefault) await PromoteDefaultAsync(bom.VariantId, id);
            await tx.CommitAsync();
        }
        return await GetBomAsync(id);
    }

    /// <summary>BOMs that work orders used are kept; deactivate them instead.</summary>
    public async Task RemoveBomAsync(Guid id)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var bom = await db.BillOfMaterials.FirstOrDefaultAsync(b => b.Id == id) ?? throw new NotFoundAppException("Ürün ağacı bulunamadı");
        Guard.State(!await db.WorkOrders.AnyAsync(w => w.BomId == id), "İş emirleri bu ürün ağacını kullanıyor; pasifleştirin");
        db.BillOfMaterials.Remove(bom);
        await db.SaveChangesAsync();
        if (bom.IsDefault) await PromoteDefaultAsync(bom.VariantId, id);
        await tx.CommitAsync();
    }

    private static List<BomLine> LineData(List<BomLineInput> lines, Guid bomId) =>
        lines.Select((l, i) => new BomLine { BomId = bomId, LineNo = i + 1, VariantId = l.VariantId, Quantity = l.Quantity, ScrapRate = l.ScrapRate ?? 0, Notes = TrimOrNull(l.Notes) }).ToList();

    private async Task<ProductVariant> LoadOutputAsync(Guid variantId)
    {
        var variant = await db.ProductVariants.AsNoTracking().Include(v => v.Product).ThenInclude(p => p.Unit).FirstOrDefaultAsync(v => v.Id == variantId);
        Guard.That(variant != null, "Ürün bulunamadı");
        AssertProducible(variant!, "Ürün");
        return variant!;
    }

    private async Task ValidateLinesAsync(Guid outputId, List<BomLineInput> lines, Guid? bomId)
    {
        var ids = lines.Select(l => l.VariantId).ToList();
        Guard.That(ids.Distinct().Count() == ids.Count, "Bir bileşen yalnızca bir kez listelenebilir");
        Guard.That(!ids.Contains(outputId), "Ürün kendi bileşeni olamaz");
        Guard.That(lines.All(l => l.Quantity > 0), "Bileşen miktarı sıfırdan büyük olmalı");
        Guard.That(lines.All(l => l.ScrapRate is null or >= 0), "Fire oranı negatif olamaz");
        var variants = await LoadVariantsAsync(ids);
        Guard.That(variants.Count == ids.Count, "Bileşen bulunamadı");
        foreach (var variant in variants.Values) AssertProducible(variant, variant.Sku);

        var others = await db.BillOfMaterials.AsNoTracking().Include(b => b.Lines).Where(b => b.IsActive && b.Id != bomId).ToListAsync();
        var graph = new Dictionary<Guid, List<Guid>>();
        foreach (var other in others)
        {
            if (!graph.TryGetValue(other.VariantId, out var list)) graph[other.VariantId] = list = [];
            list.AddRange(other.Lines.Select(l => l.VariantId));
        }
        Guard.That(!ProductionMath.CreatesCycle(outputId, ids, v => graph.GetValueOrDefault(v) ?? []), "Bir bileşen bu üründen üretiliyor; ürün ağacı döngü oluşturur");
    }

    private async Task<string> NextCodeAsync(ProductVariant output)
    {
        for (var n = 1; ; n++)
        {
            var code = $"{output.Sku}-R{n}".ToUpperInvariant();
            if (!await db.BillOfMaterials.AnyAsync(b => b.Code == code)) return code;
        }
    }

    private async Task AssertCodeFreeAsync(string code, Guid? exceptId) =>
        Guard.State(!await db.BillOfMaterials.AnyAsync(b => b.Code == code && b.Id != exceptId), $"{code} ürün ağacı kodu zaten kullanılıyor");

    private async Task ClearDefaultAsync(Guid variantId)
    {
        foreach (var other in await db.BillOfMaterials.Where(b => b.VariantId == variantId && b.IsDefault).ToListAsync()) other.IsDefault = false;
        await db.SaveChangesAsync();
    }

    /// <summary>Makes the latest other active BOM of the variant the default.</summary>
    private async Task PromoteDefaultAsync(Guid variantId, Guid except)
    {
        var next = await db.BillOfMaterials.Where(b => b.VariantId == variantId && b.IsActive && b.Id != except).OrderByDescending(b => b.UpdatedAt).FirstOrDefaultAsync();
        if (next == null) return;
        next.IsDefault = true;
        await db.SaveChangesAsync();
    }
}
