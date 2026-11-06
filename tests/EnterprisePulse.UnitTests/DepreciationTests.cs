using EnterprisePulse.Core.Modules.Assets;

namespace EnterprisePulse.UnitTests;

public class DepreciationTests
{
    private static DateOnly D(string s) => DateOnly.Parse(s);

    [Fact]
    public void Months_between_counts_whole_months_only()
    {
        Assert.Equal(1, Depreciation.MonthsBetween(D("2026-01-15"), D("2026-03-14")));
        Assert.Equal(2, Depreciation.MonthsBetween(D("2026-01-15"), D("2026-03-15")));
        Assert.Equal(3, Depreciation.MonthsBetween(D("2025-11-01"), D("2026-02-01")));
    }

    [Fact]
    public void Depreciates_linearly_per_month()
    {
        var r = Depreciation.Calculate(36000, 0, 36, D("2025-01-01"), D("2026-01-01"));
        Assert.Equal(1000m, r.MonthlyAmount);
        Assert.Equal(12, r.ElapsedMonths);
        Assert.Equal(12000m, r.Accumulated);
        Assert.Equal(24000m, r.BookValue);
        Assert.False(r.FullyDepreciated);
    }

    [Fact]
    public void Stops_at_salvage_value_after_useful_life()
    {
        var r = Depreciation.Calculate(36000, 6000, 36, D("2025-01-01"), D("2030-06-01"));
        Assert.Equal(36, r.ElapsedMonths);
        Assert.Equal(6000m, r.BookValue);
        Assert.True(r.FullyDepreciated);
    }

    [Fact]
    public void Is_zero_before_purchase_date()
    {
        var r = Depreciation.Calculate(36000, 0, 36, D("2025-01-01"), D("2024-06-01"));
        Assert.Equal(0m, r.Accumulated);
        Assert.Equal(36000m, r.BookValue);
    }

    [Fact]
    public void Schedule_splits_across_calendar_years_and_sums_to_base()
    {
        var r = Depreciation.Calculate(10000, 0, 24, D("2025-10-01"), D("2026-01-01"));
        Assert.Equal([(2025, 3), (2026, 12), (2027, 9)], r.Schedule.Select(y => (y.Year, y.Months)));
        Assert.Equal(10000m, r.Schedule.Sum(y => y.Depreciation));
        Assert.Equal(0m, r.Schedule[^1].BookValueEnd);
    }

    [Fact]
    public void Last_month_absorbs_rounding()
    {
        var r = Depreciation.Calculate(1000, 0, 3, D("2025-01-01"), D("2026-01-01"));
        Assert.Equal(333.33m, r.MonthlyAmount);
        Assert.Equal(0m, r.BookValue);
    }
}
