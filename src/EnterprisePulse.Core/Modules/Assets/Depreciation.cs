namespace EnterprisePulse.Core.Modules.Assets;

public sealed record DepreciationYear(int Year, int Months, decimal Depreciation, decimal BookValueEnd);

public sealed record DepreciationResult(
    int UsefulLifeMonths, decimal MonthlyAmount, int ElapsedMonths, decimal Accumulated, decimal BookValue, bool FullyDepreciated,
    List<DepreciationYear> Schedule);

/// <summary>Straight-line depreciation (doğrusal amortisman) from the purchase month over the useful life.</summary>
public static class Depreciation
{
    /// <summary>Whole months between two dates; a month counts once its day-of-month has been reached.</summary>
    public static int MonthsBetween(DateOnly from, DateOnly to)
    {
        var months = (to.Year - from.Year) * 12 + (to.Month - from.Month);
        return to.Day < from.Day ? months - 1 : months;
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static DepreciationResult Calculate(decimal cost, decimal salvage, int usefulLifeMonths, DateOnly purchaseDate, DateOnly asOf)
    {
        var life = Math.Max(1, usefulLifeMonths);
        var depreciable = Math.Max(cost - salvage, 0m);
        var monthly = depreciable / life;
        var elapsed = Math.Min(life, Math.Max(0, MonthsBetween(purchaseDate, asOf)));
        // The last month absorbs rounding so the book value lands exactly on the salvage value.
        decimal AccumulatedFor(int months) => months >= life ? depreciable : Round(monthly * months);

        var schedule = new List<DepreciationYear>();
        var consumed = 0;
        for (var year = purchaseDate.Year; consumed < life; year++)
        {
            var available = year == purchaseDate.Year ? 13 - purchaseDate.Month : 12;
            var months = Math.Min(available, life - consumed);
            var before = AccumulatedFor(consumed);
            consumed += months;
            var after = AccumulatedFor(consumed);
            schedule.Add(new DepreciationYear(year, months, after - before, cost - after));
        }

        var accumulated = AccumulatedFor(elapsed);
        return new DepreciationResult(life, Round(monthly), elapsed, accumulated, cost - accumulated, elapsed >= life, schedule);
    }
}
