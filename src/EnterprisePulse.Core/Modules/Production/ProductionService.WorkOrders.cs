using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Accounting;
using EnterprisePulse.Core.Modules.Inventory;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Purchasing;
using EnterprisePulse.Core.Modules.Trade;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Production;

public sealed partial class ProductionService
{
    /// <summary>The "Üretim fişi" series is not part of the standard company sequences; creates it on first use.</summary>
    private async Task EnsureRecordSequenceAsync(Guid companyId)
    {
        if (await db.NumberSequences.AnyAsync(n => n.Code == RecordSequence)) return;
        db.NumberSequences.Add(new NumberSequence { CompanyId = companyId, Code = RecordSequence, Name = "Üretim Fişi", Prefix = "URT", Padding = 6, IncludeYear = true, ResetYearly = true, CurrentYear = DateTime.UtcNow.Year });
        await db.SaveChangesAsync();
    }

    private IQueryable<WorkOrder> OrderQuery() =>
        db.WorkOrders.Include(w => w.Bom).Include(w => w.SourceWarehouse).Include(w => w.TargetWarehouse)
            .Include(w => w.Variant).ThenInclude(v => v.Product).ThenInclude(p => p.Unit);

    private static WorkOrderDto ToDto(WorkOrder w, DateOnly today) =>
        new(w.Id, w.Number, w.Status, w.BomId, w.Bom.Code, Info(w.Variant), w.Quantity, w.ProducedQty, ProductionMath.Progress(w.ProducedQty, w.Quantity), w.PlannedStart, w.PlannedEnd,
            ProductionMath.IsLate(w.Status, w.PlannedEnd, today), w.SourceWarehouseId, w.SourceWarehouse.Name, w.TargetWarehouseId, w.TargetWarehouse.Name, w.Reference, w.CreatedAt);

    public async Task<WorkOrderDetailDto> GetWorkOrderAsync(Guid id)
    {
        var w = await OrderQuery().AsNoTracking().AsSplitQuery().Include(x => x.CreatedBy)
            .Include(x => x.Components).Include(x => x.Records).ThenInclude(r => r.CreatedBy)
            .Include(x => x.Records).ThenInclude(r => r.IssueMovement).Include(x => x.Records).ThenInclude(r => r.ReceiptMovement)
            .FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundAppException("İş emri bulunamadı");
        var ids = w.Components.Select(c => c.VariantId).ToList();
        var variants = await LoadVariantsAsync(ids);
        var onHand = await OnHandAsync(ids, w.SourceWarehouseId);
        var book = await LoadCostBookAsync(ids);
        var lotTracked = w.Variant.Product.TrackingType == TrackingType.LOT;

        var components = w.Components.OrderBy(c => c.LineNo).Select(c =>
        {
            var remaining = Math.Max(c.RequiredQty - c.ConsumedQty, 0m);
            var stockHere = onHand.GetValueOrDefault(c.VariantId);
            return new WorkOrderComponentDto(c.Id, c.LineNo, Info(variants[c.VariantId]), c.QuantityPer, c.RequiredQty, c.ConsumedQty, remaining, stockHere,
                ProductionMath.Availability(remaining, stockHere), variants[c.VariantId].Product.TrackingType == TrackingType.LOT);
        }).ToList();
        var records = w.Records.OrderBy(r => r.RecordDate).ThenBy(r => r.CreatedAt).Select(r =>
            new ProductionRecordDto(r.Id, r.Number, r.RecordDate, r.Quantity, r.MaterialCost, r.LaborCost, r.OverheadCost, r.MaterialCost + r.LaborCost + r.OverheadCost, r.UnitCost,
                r.IssueMovementId, r.IssueMovement?.Number, r.ReceiptMovementId, r.ReceiptMovement.Number, null, r.Notes, $"{r.CreatedBy.FirstName} {r.CreatedBy.LastName}", r.CreatedAt)).ToList();

        var standard = DocumentMath.Round4(components.Sum(c => c.QuantityPer * book.Cost(c.Variant.VariantId)) + w.LaborPerUnit + w.OverheadPerUnit);
        var produced = records.Sum(r => r.TotalCost);
        decimal? actual = w.ProducedQty > 0 ? DocumentMath.Round4(produced / w.ProducedQty) : null;
        var buildable = Math.Min(ProductionMath.Buildable(components.Select(c => new BuildLine(c.QuantityPer, c.OnHand))), Math.Max(w.Quantity - w.ProducedQty, 0m));
        return new WorkOrderDetailDto(ToDto(w, AccountingClock.Today), w.Notes, w.LaborPerUnit, w.OverheadPerUnit, standard, actual, lotTracked, $"{w.CreatedBy.FirstName} {w.CreatedBy.LastName}",
            w.ReleasedAt, w.StartedAt, w.CompletedAt, w.CancelledAt, components, records, buildable, ProductionMath.CanEdit(w.Status), ProductionMath.CanRelease(w.Status),
            ProductionMath.CanRecord(w.Status), ProductionMath.CanCancel(w.Status, w.ProducedQty), ProductionMath.CanComplete(w.Status));
    }

    public async Task<WorkOrderDetailDto> CreateWorkOrderAsync(CreateWorkOrder dto)
    {
        var companyId = Ctx.RequireCompanyId();
        Guard.That(dto.Quantity > 0, "Miktar sıfırdan büyük olmalı");
        Guard.That(dto.PlannedEnd >= dto.PlannedStart, "Planlanan bitiş, başlangıçtan önce olamaz");
        Guid id;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var bom = await db.BillOfMaterials.Include(b => b.Lines).Include(b => b.Variant).ThenInclude(v => v.Product).FirstOrDefaultAsync(b => b.Id == dto.BomId)
                ?? throw new NotFoundAppException("Ürün ağacı bulunamadı");
            Guard.That(bom.IsActive, "Pasif ürün ağacıyla iş emri açılamaz");
            await AssertWarehousesAsync(dto.SourceWarehouseId, dto.TargetWarehouseId);

            var order = new WorkOrder
            {
                CompanyId = companyId, Number = await numbering.NextAsync("WORK_ORDER"), BomId = bom.Id, VariantId = bom.VariantId, Quantity = dto.Quantity, PlannedStart = dto.PlannedStart,
                PlannedEnd = dto.PlannedEnd, SourceWarehouseId = dto.SourceWarehouseId, TargetWarehouseId = dto.TargetWarehouseId,
                LaborPerUnit = ProductionMath.CostPerUnit(bom.LaborCost, bom.Quantity), OverheadPerUnit = ProductionMath.CostPerUnit(bom.OverheadCost, bom.Quantity),
                Reference = TrimOrNull(dto.Reference), Notes = TrimOrNull(dto.Notes), CreatedById = Ctx.RequireUserId(),
            };
            order.Components.AddRange(bom.Lines.OrderBy(l => l.LineNo).Select(l =>
            {
                var per = ProductionMath.PerUnit(l.Quantity, l.ScrapRate, bom.Quantity);
                return new WorkOrderComponent { WorkOrderId = order.Id, LineNo = l.LineNo, VariantId = l.VariantId, QuantityPer = per, RequiredQty = ProductionMath.Required(per, dto.Quantity) };
            }));
            db.WorkOrders.Add(order);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            id = order.Id;
        }
        return await GetWorkOrderAsync(id);
    }

    public async Task<WorkOrderDetailDto> UpdateWorkOrderAsync(Guid id, UpdateWorkOrder dto)
    {
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var order = await LockOrderAsync(id);
            Guard.State(ProductionMath.CanEdit(order.Status), "Yalnızca taslak iş emirleri düzenlenebilir");
            Guard.That(dto.Quantity == null || dto.Quantity > 0, "Miktar sıfırdan büyük olmalı");
            var start = dto.PlannedStart ?? order.PlannedStart;
            var end = dto.PlannedEnd ?? order.PlannedEnd;
            Guard.That(end >= start, "Planlanan bitiş, başlangıçtan önce olamaz");
            var source = dto.SourceWarehouseId ?? order.SourceWarehouseId;
            var target = dto.TargetWarehouseId ?? order.TargetWarehouseId;
            if (source != order.SourceWarehouseId || target != order.TargetWarehouseId) await AssertWarehousesAsync(source, target);

            order.PlannedStart = start;
            order.PlannedEnd = end;
            order.SourceWarehouseId = source;
            order.TargetWarehouseId = target;
            if (dto.Reference != null) order.Reference = TrimOrNull(dto.Reference);
            if (dto.Notes != null) order.Notes = TrimOrNull(dto.Notes);
            if (dto.Quantity != null && dto.Quantity != order.Quantity)
            {
                order.Quantity = dto.Quantity.Value;
                foreach (var c in order.Components) c.RequiredQty = ProductionMath.Required(c.QuantityPer, order.Quantity);
            }
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        return await GetWorkOrderAsync(id);
    }

    public async Task<WorkOrderDetailDto> ReleaseAsync(Guid id)
    {
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var order = await LockOrderAsync(id);
            Guard.State(ProductionMath.CanRelease(order.Status), "Yalnızca taslak iş emirleri serbest bırakılabilir");
            await AssertWarehousesAsync(order.SourceWarehouseId, order.TargetWarehouseId);
            order.Status = WorkOrderStatus.RELEASED;
            order.ReleasedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        return await GetWorkOrderAsync(id);
    }

    public async Task<WorkOrderDetailDto> CancelAsync(Guid id)
    {
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var order = await LockOrderAsync(id);
            Guard.State(ProductionMath.CanCancel(order.Status, order.ProducedQty), "Üretim kaydı olan iş emri iptal edilemez; tamamlayın");
            order.Status = WorkOrderStatus.CANCELLED;
            order.CancelledAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        return await GetWorkOrderAsync(id);
    }

    /// <summary>Closes an order that produced less than planned.</summary>
    public async Task<WorkOrderDetailDto> CompleteAsync(Guid id)
    {
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var order = await LockOrderAsync(id);
            Guard.State(ProductionMath.CanComplete(order.Status), "Yalnızca üretimdeki iş emirleri tamamlanabilir");
            order.Status = WorkOrderStatus.COMPLETED;
            order.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        return await GetWorkOrderAsync(id);
    }

    /// <summary>
    /// Reports output. Issues the materials from the source warehouse (first-expiring lots first for lot-tracked ones) and receives the product
    /// into the target warehouse at material + labour + overhead cost per unit.
    /// </summary>
    public async Task<WorkOrderDetailDto> RecordAsync(Guid id, RecordProduction dto)
    {
        var companyId = Ctx.RequireCompanyId();
        await EnsureRecordSequenceAsync(companyId);
        List<LowStockItem> low;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var order = await LockOrderAsync(id);
            Guard.State(ProductionMath.CanRecord(order.Status), "Yalnızca serbest bırakılmış veya üretimdeki iş emirlerine üretim girilebilir");
            var output = dto.Quantity;
            Guard.That(output > 0, "Miktar sıfırdan büyük olmalı");
            var remaining = order.Quantity - order.ProducedQty;
            Guard.That(output <= remaining, $"En fazla {StockMovementService.Qty(remaining)} üretilebilir");
            var finishes = output == remaining || dto.Finish == true;
            var recordDate = dto.RecordDate ?? AccountingClock.Today;

            var product = await db.ProductVariants.AsNoTracking().Include(v => v.Product).FirstAsync(v => v.Id == order.VariantId);
            var lotTracked = product.Product.TrackingType == TrackingType.LOT;
            var lotNo = TrimOrNull(dto.LotNo) ?? order.Number;

            var overrides = (dto.Components ?? []).ToDictionary(c => c.VariantId, c => c.Quantity);
            Guard.That(overrides.Keys.All(v => order.Components.Any(c => c.VariantId == v)), "İş emrinde olmayan bir bileşen girildi");
            Guard.That(overrides.Values.All(q => q >= 0), "Sarf miktarı negatif olamaz");

            var variants = await LoadVariantsAsync(order.Components.Select(c => c.VariantId));
            var issueLines = new List<MovementLineInput>();
            var used = new Dictionary<WorkOrderComponent, decimal>();
            foreach (var c in order.Components.OrderBy(c => c.LineNo))
            {
                var qty = overrides.TryGetValue(c.VariantId, out var actual)
                    ? actual
                    : ProductionMath.StandardConsumption(c.QuantityPer, c.RequiredQty, c.ConsumedQty, output, finishes);
                if (qty <= 0) continue;
                used[c] = qty;
                if (variants[c.VariantId].Product.TrackingType == TrackingType.LOT)
                    issueLines.AddRange(await AllocateLotsAsync(order.SourceWarehouseId, variants[c.VariantId], qty));
                else
                    issueLines.Add(new MovementLineInput(c.VariantId, qty));
            }

            var number = await numbering.NextAsync(RecordSequence);
            var notes = $"{order.Number} üretimi";
            PostResult? issue = null;
            decimal material = 0;
            if (issueLines.Count > 0)
            {
                issue = await stock.PostAsync(new CreateMovementRequest(StockMovementType.ISSUE, recordDate, order.SourceWarehouseId, null, order.Number, $"{notes} — malzeme sarfı", issueLines));
                low = issue.LowStock;
                material = DocumentMath.Round2(await db.StockMovementLines.Where(l => l.MovementId == issue.MovementId).SumAsync(l => l.Quantity * (l.UnitCost ?? 0m)));
            }
            else low = [];

            var labor = DocumentMath.Round2(order.LaborPerUnit * output);
            var overhead = DocumentMath.Round2(order.OverheadPerUnit * output);
            var unitCost = DocumentMath.Round4((material + labor + overhead) / output);
            var receipt = await stock.PostAsync(new CreateMovementRequest(StockMovementType.RECEIPT, recordDate, null, order.TargetWarehouseId, order.Number, $"{notes} — mamul girişi",
                [new MovementLineInput(order.VariantId, output, unitCost, LotNo: lotTracked ? lotNo : null, ExpiryDate: lotTracked ? dto.ExpiryDate : null)]));
            low = [.. low, .. receipt.LowStock];

            db.ProductionRecords.Add(new ProductionRecord
            {
                CompanyId = companyId, WorkOrderId = order.Id, Number = number, RecordDate = recordDate, Quantity = output, MaterialCost = material, LaborCost = labor, OverheadCost = overhead,
                UnitCost = unitCost, IssueMovementId = issue?.MovementId, ReceiptMovementId = receipt.MovementId, Notes = TrimOrNull(dto.Notes), CreatedById = Ctx.RequireUserId(),
            });
            foreach (var (component, qty) in used) component.ConsumedQty += qty;
            order.ProducedQty += output;
            order.StartedAt ??= DateTime.UtcNow;
            if (finishes)
            {
                order.Status = WorkOrderStatus.COMPLETED;
                order.CompletedAt = DateTime.UtcNow;
            }
            else order.Status = WorkOrderStatus.IN_PROGRESS;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        await stock.NotifyLowStockAsync(low);
        return await GetWorkOrderAsync(id);
    }

    /// <summary>Lot-tracked components leave the lots that expire first.</summary>
    private async Task<List<MovementLineInput>> AllocateLotsAsync(Guid warehouseId, ProductVariant variant, decimal quantity)
    {
        var balances = await (from b in db.LotBalances
                              join l in db.StockLots on b.LotId equals l.Id
                              where b.WarehouseId == warehouseId && b.Quantity > 0 && l.VariantId == variant.Id
                              select new { b.LotId, b.Quantity, l.LotNo, l.ExpiryDate }).ToListAsync();
        var lines = new List<MovementLineInput>();
        var left = quantity;
        foreach (var lot in balances.OrderBy(b => b.ExpiryDate ?? DateOnly.MaxValue).ThenBy(b => b.LotNo, StringComparer.Ordinal))
        {
            if (left <= 0) break;
            var take = Math.Min(lot.Quantity, left);
            lines.Add(new MovementLineInput(variant.Id, take, LotId: lot.LotId));
            left -= take;
        }
        Guard.That(left <= 0, $"{variant.Sku} için kaynak depoda yeterli lot stoğu yok");
        return lines;
    }

    private async Task<WorkOrder> LockOrderAsync(Guid id)
    {
        await db.Database.ExecuteSqlAsync($"SELECT id FROM work_orders WHERE id = {id} FOR UPDATE");
        return await db.WorkOrders.Include(w => w.Components).FirstOrDefaultAsync(w => w.Id == id) ?? throw new NotFoundAppException("İş emri bulunamadı");
    }

    private async Task AssertWarehousesAsync(params Guid[] ids)
    {
        var list = ids.Distinct().ToList();
        var rows = await db.Warehouses.AsNoTracking().Where(w => list.Contains(w.Id)).ToListAsync();
        Guard.That(rows.Count == list.Count, "Depo bulunamadı");
        Guard.That(rows.All(w => w.IsActive), "Pasif depo seçilemez");
    }

    // ---------- Requirements (MRP) ----------

    private sealed class Demand
    {
        public decimal Production, Sales, ProductionIncoming;
        public DateOnly? NeededBy;
        public List<RequirementOrderDto> WorkOrders = [], SalesOrders = [];
    }

    /// <summary>
    /// Net requirements of every item open work orders consume and every made product approved sales orders still wait for.
    /// Shortages first, then by the date they are needed.
    /// </summary>
    public async Task<List<RequirementDto>> RequirementsAsync()
    {
        var orders = await db.WorkOrders.AsNoTracking().Include(w => w.Components).Where(w => ProductionMath.OpenStatuses.Contains(w.Status))
            .OrderBy(w => w.PlannedStart).ThenBy(w => w.Number).ToListAsync();
        var boms = await DefaultBomsAsync();
        var demands = new Dictionary<Guid, Demand>();
        Demand DemandOf(Guid variantId)
        {
            if (!demands.TryGetValue(variantId, out var d)) demands[variantId] = d = new Demand();
            return d;
        }
        static DateOnly Earliest(DateOnly? a, DateOnly b) => a != null && a <= b ? a.Value : b;

        var output = new Dictionary<Guid, decimal>();
        foreach (var order in orders)
        {
            output[order.VariantId] = output.GetValueOrDefault(order.VariantId) + Math.Max(order.Quantity - order.ProducedQty, 0m);
            foreach (var c in order.Components)
            {
                var remaining = Math.Max(c.RequiredQty - c.ConsumedQty, 0m);
                if (remaining == 0) continue;
                var d = DemandOf(c.VariantId);
                d.Production += remaining;
                d.NeededBy = Earliest(d.NeededBy, order.PlannedStart);
                d.WorkOrders.Add(new RequirementOrderDto(order.Id, order.Number, remaining, order.PlannedStart));
            }
        }

        var madeIds = boms.Keys.ToList();
        var salesLines = await db.SalesOrderLines.AsNoTracking().Include(l => l.Order)
            .Where(l => madeIds.Contains(l.VariantId) && (l.Order.Status == SalesOrderStatus.APPROVED || l.Order.Status == SalesOrderStatus.PARTIALLY_DELIVERED))
            .OrderBy(l => l.Order.OrderDate).ToListAsync();
        foreach (var line in salesLines)
        {
            var remaining = Math.Max(line.Quantity - line.DeliveredQty, 0m);
            if (remaining == 0) continue;
            var d = DemandOf(line.VariantId);
            var date = line.Order.DeliveryDate ?? line.Order.OrderDate;
            d.Sales += remaining;
            d.NeededBy = Earliest(d.NeededBy, date);
            d.SalesOrders.Add(new RequirementOrderDto(line.Order.Id, line.Order.Number, remaining, date));
        }
        foreach (var (variantId, quantity) in output)
            if (demands.TryGetValue(variantId, out var d)) d.ProductionIncoming = quantity;

        var ids = demands.Keys.ToList();
        if (ids.Count == 0) return [];
        var onHand = await OnHandAsync(ids);
        var purchaseLines = await db.PurchaseOrderLines.AsNoTracking()
            .Where(l => ids.Contains(l.VariantId) && (l.Order.Status == PurchaseOrderStatus.APPROVED || l.Order.Status == PurchaseOrderStatus.PARTIALLY_RECEIVED))
            .Select(l => new { l.VariantId, l.Quantity, l.ReceivedQty }).ToListAsync();
        var incoming = new Dictionary<Guid, decimal>();
        foreach (var l in purchaseLines) incoming[l.VariantId] = incoming.GetValueOrDefault(l.VariantId) + Math.Max(l.Quantity - l.ReceivedQty, 0m);
        var variants = await LoadVariantsAsync(ids);

        return variants.Values.Select(v =>
            {
                var d = demands[v.Id];
                var gross = d.Production + d.Sales;
                var stockHere = onHand.GetValueOrDefault(v.Id);
                var purchase = incoming.GetValueOrDefault(v.Id);
                var shortage = ProductionMath.NetRequirement(gross, stockHere, purchase + d.ProductionIncoming);
                boms.TryGetValue(v.Id, out var bom);
                return new RequirementDto(Info(v), d.Production, d.Sales, gross, stockHere, purchase, d.ProductionIncoming, shortage, d.NeededBy, bom != null, bom?.Id, v.Product.PurchasePrice,
                    d.WorkOrders, d.SalesOrders);
            })
            .OrderByDescending(r => r.Shortage > 0).ThenBy(r => r.NeededBy ?? DateOnly.MaxValue).ThenBy(r => r.Variant.Sku, StringComparer.Ordinal).ToList();
    }

    /// <summary>Turns shortages of bought items into one purchase request.</summary>
    public async Task<CreatedDocumentDto> CreatePurchaseRequestAsync(CreateRequirementRequest dto)
    {
        var ids = dto.Lines.Select(l => l.VariantId).ToList();
        Guard.That(ids.Count > 0, "En az bir satır gerekli");
        Guard.That(ids.Distinct().Count() == ids.Count, "Bir ürün yalnızca bir kez listelenebilir");
        var variants = await LoadVariantsAsync(ids);
        Guard.That(variants.Count == ids.Count, "Ürün bulunamadı");
        var created = await requests.CreateAsync(new CreatePurchaseRequest("Üretim malzeme ihtiyacı (MRP)", dto.NeededBy, null, "Malzeme ihtiyaç planlamasındaki eksiklerden oluşturuldu.",
            dto.Lines.Select(l => new RequestLineInput(l.VariantId, null, l.Quantity, variants[l.VariantId].Product.PurchasePrice)).ToList()));
        return new CreatedDocumentDto(created.Id, created.Number);
    }

    public async Task<ProductionSummaryDto> SummaryAsync()
    {
        var today = AccountingClock.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthStartUtc = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var statuses = await db.WorkOrders.AsNoTracking().Where(w => ProductionMath.OpenStatuses.Contains(w.Status)).GroupBy(w => w.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        var late = await db.WorkOrders.CountAsync(w => ProductionMath.OpenStatuses.Contains(w.Status) && w.PlannedEnd < today);
        var completed = await db.WorkOrders.CountAsync(w => w.Status == WorkOrderStatus.COMPLETED && w.CompletedAt >= monthStartUtc);
        var value = await db.ProductionRecords.Where(r => r.RecordDate >= monthStart).SumAsync(r => (decimal?)(r.MaterialCost + r.LaborCost + r.OverheadCost)) ?? 0m;
        var activeBoms = await db.BillOfMaterials.CountAsync(b => b.IsActive);
        var upcoming = await OrderQuery().AsNoTracking().Where(w => ProductionMath.OpenStatuses.Contains(w.Status)).OrderBy(w => w.PlannedStart).ThenBy(w => w.Number).Take(5).ToListAsync();
        var shortages = (await RequirementsAsync()).Count(r => r.Shortage > 0);
        int Count(WorkOrderStatus s) => statuses.FirstOrDefault(x => x.Key == s)?.Count ?? 0;
        return new ProductionSummaryDto(activeBoms, Count(WorkOrderStatus.DRAFT), Count(WorkOrderStatus.RELEASED), Count(WorkOrderStatus.IN_PROGRESS), late, completed, DocumentMath.Round2(value), shortages,
            upcoming.Select(w => ToDto(w, today)).ToList());
    }
}
