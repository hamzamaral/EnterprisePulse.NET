namespace EnterprisePulse.Core.Modules.Inventory;

/// <summary>Quantities carry 3 decimals, unit costs 4.</summary>
public static class StockMath
{
    /// <summary>
    /// Moving weighted average after a receipt. A non-positive on-hand quantity carries no value, so the receipt
    /// cost becomes the new average.
    /// </summary>
    public static decimal WeightedAverageCost(decimal onHand, decimal average, decimal received, decimal cost)
    {
        if (onHand <= 0) return Math.Round(cost, 4, MidpointRounding.AwayFromZero);
        var total = onHand + received;
        if (total <= 0) return average;
        return Math.Round((onHand * average + received * cost) / total, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>True when a movement takes stock from at/above the reorder point to below it.</summary>
    public static bool CrossedBelowMinimum(decimal before, decimal after, decimal minimum) =>
        minimum > 0 && before >= minimum && after < minimum;

    public static bool IsWhole(decimal v) => v == Math.Truncate(v);
}
