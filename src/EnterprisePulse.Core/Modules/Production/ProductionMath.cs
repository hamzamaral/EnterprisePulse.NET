using EnterprisePulse.Core.Data;

namespace EnterprisePulse.Core.Modules.Production;

public sealed record BomLineShape(Guid VariantId, decimal Quantity, decimal ScrapRate);

/// <summary>A BOM as the calculations see it; <see cref="Quantity"/> is the output of one batch.</summary>
public sealed record BomShape(decimal Quantity, decimal LaborCost, decimal OverheadCost, List<BomLineShape> Lines)
{
    public Guid Id { get; init; }

    public Guid VariantId { get; init; }
}

public sealed record RolledCost(decimal Material, decimal Labor, decimal Overhead, decimal UnitCost);

public sealed record ExplodedLine(int Level, Guid VariantId, Guid ParentId, decimal Quantity, decimal Allocated, decimal Shortage, bool Made);

public sealed record BuildLine(decimal QuantityPer, decimal Available);

/// <summary>BOM, work order and MRP arithmetic. Rounding follows the original (half up for costs, up for requirements).</summary>
public static class ProductionMath
{
    private static decimal Round(decimal v, int places) => Math.Round(v, places, MidpointRounding.AwayFromZero);

    /// <summary>Planned use of a BOM line per unit of output: quantity × (1 + scrap %) / batch.</summary>
    public static decimal PerUnit(decimal quantity, decimal scrapRate, decimal batch) => Round(quantity * (1 + scrapRate / 100m) / batch, 6);

    /// <summary>Quantity needed for <paramref name="output"/> units; rounded up so the plan never falls short.</summary>
    public static decimal Required(decimal quantityPer, decimal output)
    {
        var raw = quantityPer * output;
        var scaled = Math.Ceiling(Math.Abs(raw) * 1000m) / 1000m;
        return raw < 0 ? -scaled : scaled;
    }

    /// <summary>Labour or overhead per unit of output.</summary>
    public static decimal CostPerUnit(decimal batchCost, decimal batch) => Round(batchCost / batch, 4);

    /// <summary>
    /// Standard consumption of one component for a production record. The record that finishes the order uses up whatever is left of
    /// the requirement, so the totals match the plan exactly.
    /// </summary>
    public static decimal StandardConsumption(decimal quantityPer, decimal requiredQty, decimal consumedQty, decimal output, bool finishes)
    {
        var remaining = Math.Max(requiredQty - consumedQty, 0m);
        return finishes ? remaining : Math.Min(Required(quantityPer, output), remaining);
    }

    /// <summary>
    /// Standard unit cost of a BOM: components at their cost plus labour and overhead, divided by the batch. <paramref name="costOf"/>
    /// returns the unit cost of a component (average cost or its own rolled-up cost).
    /// </summary>
    public static RolledCost RollUpCost(BomShape bom, Func<Guid, decimal> costOf)
    {
        var material = bom.Lines.Sum(l => l.Quantity * (1 + l.ScrapRate / 100m) * costOf(l.VariantId));
        return new RolledCost(Round(material, 2), Round(bom.LaborCost, 2), Round(bom.OverheadCost, 2), Round((material + bom.LaborCost + bom.OverheadCost) / bom.Quantity, 4));
    }

    /// <summary>True when using <paramref name="components"/> to make <paramref name="output"/> would close a loop through the default BOMs.</summary>
    public static bool CreatesCycle(Guid output, IEnumerable<Guid> components, Func<Guid, IEnumerable<Guid>> componentsOf)
    {
        var seen = new HashSet<Guid>();
        var stack = new Stack<Guid>(components);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == output) return true;
            if (!seen.Add(current)) continue;
            foreach (var next in componentsOf(current)) stack.Push(next);
        }
        return false;
    }

    /// <summary>
    /// Multi-level net explosion of <paramref name="quantity"/> units of a variant (depth first, in BOM line order). Each line takes what
    /// it can from <paramref name="stock"/>; only the shortage of a made component is exploded further.
    /// </summary>
    public static List<ExplodedLine> Explode(Guid variantId, decimal quantity, Func<Guid, BomShape?> bomOf, Dictionary<Guid, decimal> stock, int maxDepth = 10)
    {
        var left = new Dictionary<Guid, decimal>(stock);
        var result = new List<ExplodedLine>();
        void Walk(Guid parentId, decimal output, int level)
        {
            var bom = bomOf(parentId);
            if (bom == null || level > maxDepth) return;
            foreach (var line in bom.Lines)
            {
                var need = Required(PerUnit(line.Quantity, line.ScrapRate, bom.Quantity), output);
                var free = Math.Max(left.GetValueOrDefault(line.VariantId), 0m);
                var allocated = Math.Min(free, need);
                left[line.VariantId] = free - allocated;
                var shortage = need - allocated;
                var made = bomOf(line.VariantId) != null;
                result.Add(new ExplodedLine(level, line.VariantId, parentId, need, allocated, shortage, made));
                if (made && shortage > 0) Walk(line.VariantId, shortage, level + 1);
            }
        }
        Walk(variantId, quantity, 1);
        return result;
    }

    /// <summary>Net requirement (MRP): gross demand minus stock on hand and what is already on its way.</summary>
    public static decimal NetRequirement(decimal gross, decimal onHand, decimal incoming) => Math.Max(gross - Math.Max(onHand, 0m) - incoming, 0m);

    /// <summary>How many units the components on hand allow, given the per-unit use of each.</summary>
    public static decimal Buildable(IEnumerable<BuildLine> lines)
    {
        decimal? max = null;
        foreach (var line in lines)
        {
            if (line.QuantityPer <= 0) continue;
            var units = Math.Floor(Math.Max(line.Available, 0m) / line.QuantityPer);
            max = max == null ? units : Math.Min(max.Value, units);
        }
        return max ?? 0m;
    }

    /// <summary>Work orders whose materials still count as demand.</summary>
    public static readonly WorkOrderStatus[] OpenStatuses = [WorkOrderStatus.DRAFT, WorkOrderStatus.RELEASED, WorkOrderStatus.IN_PROGRESS];

    public static bool CanEdit(WorkOrderStatus status) => status == WorkOrderStatus.DRAFT;

    public static bool CanRelease(WorkOrderStatus status) => status == WorkOrderStatus.DRAFT;

    public static bool CanRecord(WorkOrderStatus status) => status is WorkOrderStatus.RELEASED or WorkOrderStatus.IN_PROGRESS;

    /// <summary>Orders with posted output are completed (closed short), not cancelled.</summary>
    public static bool CanCancel(WorkOrderStatus status, decimal produced) => status is WorkOrderStatus.DRAFT or WorkOrderStatus.RELEASED && produced == 0;

    public static bool CanComplete(WorkOrderStatus status) => status == WorkOrderStatus.IN_PROGRESS;

    /// <summary>Open order past its planned end date.</summary>
    public static bool IsLate(WorkOrderStatus status, DateOnly plannedEnd, DateOnly today) => OpenStatuses.Contains(status) && plannedEnd < today;

    /// <summary>Produced share of the order, 0–100.</summary>
    public static int Progress(decimal produced, decimal quantity) =>
        quantity <= 0 ? 0 : (int)Math.Min(100m, Round(produced / quantity * 100m, 0));

    /// <summary>Whether the source warehouse covers a component: enough, partly or nothing.</summary>
    public static string Availability(decimal needed, decimal onHand) =>
        needed <= 0 || onHand >= needed ? "OK" : onHand > 0 ? "PARTIAL" : "MISSING";
}
