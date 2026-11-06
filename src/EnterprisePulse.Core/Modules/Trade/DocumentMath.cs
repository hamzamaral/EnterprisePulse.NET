namespace EnterprisePulse.Core.Modules.Trade;

public sealed record LineInput(decimal Quantity, decimal UnitPrice, decimal DiscountRate, decimal VatRate);

public sealed record LineAmounts(decimal Gross, decimal Discount, decimal Net, decimal Vat, decimal Total);

public sealed record DocumentTotals(decimal Subtotal, decimal DiscountTotal, decimal VatTotal, decimal GrandTotal);

/// <summary>
/// Line and document totals of every trade document. Each line part is rounded to kuruş first and the document sums
/// the rounded parts, so the printed lines always add up to the printed totals.
/// </summary>
public static class DocumentMath
{
    public static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static decimal Round3(decimal v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);

    public static decimal Round4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

    public static LineAmounts Line(LineInput l)
    {
        var gross = Round2(l.Quantity * l.UnitPrice);
        var discount = Round2(gross * l.DiscountRate / 100m);
        var net = gross - discount;
        var vat = Round2(net * l.VatRate / 100m);
        return new LineAmounts(gross, discount, net, vat, net + vat);
    }

    public static DocumentTotals Document(IEnumerable<LineInput> lines)
    {
        decimal sub = 0, disc = 0, vat = 0;
        foreach (var a in lines.Select(Line))
        {
            sub += a.Gross;
            disc += a.Discount;
            vat += a.Vat;
        }
        return new DocumentTotals(sub, disc, vat, sub - disc + vat);
    }

    public static decimal ToBase(decimal amount, decimal rate) => Round2(amount * rate);

    /// <summary>Discounted unit price converted to the base currency: the cost a receipt books into stock.</summary>
    public static decimal NetUnitCostBase(decimal unitPrice, decimal discountRate, decimal rate) =>
        Round4(unitPrice * (100m - discountRate) / 100m * rate);
}
