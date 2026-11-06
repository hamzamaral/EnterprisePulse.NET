using EnterprisePulse.Core.Modules.Inventory;
using EnterprisePulse.Core.Modules.Trade;

namespace EnterprisePulse.UnitTests;

public class TradeMathTests
{
    [Fact]
    public void Weighted_average_replaces_cost_when_on_hand_is_not_positive()
    {
        Assert.Equal(12.3457m, StockMath.WeightedAverageCost(0, 99, 4, 12.34567m));
        Assert.Equal(8m, StockMath.WeightedAverageCost(-2, 3, 5, 8));
    }

    [Fact]
    public void Weighted_average_blends_existing_stock_at_four_decimals()
    {
        Assert.Equal(11.5m, StockMath.WeightedAverageCost(10, 10, 10, 13));
        Assert.Equal(10.3333m, StockMath.WeightedAverageCost(2, 10, 1, 11));
    }

    [Fact]
    public void Minimum_is_crossed_only_from_at_or_above_to_below()
    {
        Assert.True(StockMath.CrossedBelowMinimum(5, 4.999m, 5));
        Assert.False(StockMath.CrossedBelowMinimum(4, 3, 5));
        Assert.False(StockMath.CrossedBelowMinimum(6, 5, 5));
        Assert.False(StockMath.CrossedBelowMinimum(6, 1, 0));
    }

    [Fact]
    public void Document_rounds_each_line_before_summing()
    {
        var line = DocumentMath.Line(new LineInput(3, 10.005m, 10, 20));
        Assert.Equal(30.02m, line.Gross);
        Assert.Equal(3.00m, line.Discount);
        Assert.Equal(27.02m, line.Net);
        Assert.Equal(5.40m, line.Vat);
        Assert.Equal(32.42m, line.Total);

        var doc = DocumentMath.Document([new LineInput(1, 10.004m, 0, 20), new LineInput(1, 10.004m, 0, 20)]);
        Assert.Equal(20.00m, doc.Subtotal);
        Assert.Equal(4.00m, doc.VatTotal);
        Assert.Equal(24.00m, doc.GrandTotal);
    }

    [Fact]
    public void Net_unit_cost_is_the_discounted_base_price()
    {
        Assert.Equal(436.3200m, DocumentMath.NetUnitCostBase(10, 10, 48.48m));
        Assert.Equal(216.00m, DocumentMath.ToBase(4.5m, 48));
    }
}
