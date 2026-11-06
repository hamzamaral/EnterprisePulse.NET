using System.Text.Json.Nodes;

namespace EnterprisePulse.Web.Services;

/// <summary>One editable line of a quote, order or invoice.</summary>
public sealed class DocLine
{
    public string Key { get; set; } = Guid.NewGuid().ToString("N");
    public string? VariantId { get; set; }
    public string? Sku { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal DiscountRate { get; set; }
    public decimal VatRate { get; set; } = 20;
    public string? RequestLineId { get; set; }
    public string? OrderLineId { get; set; }
    public string? LotNo { get; set; }
    public DateTime? Expiry { get; set; }
    public string? Serials { get; set; }
    public decimal? MaxQuantity { get; set; }

    public decimal Total
    {
        get
        {
            var gross = Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
            var discount = Math.Round(gross * DiscountRate / 100m, 2, MidpointRounding.AwayFromZero);
            var net = gross - discount;
            return net + Math.Round(net * VatRate / 100m, 2, MidpointRounding.AwayFromZero);
        }
    }

    public object ToApi() => new
    {
        variantId = VariantId, description = string.IsNullOrWhiteSpace(Description) ? null : Description, quantity = Quantity, unitPrice = UnitPrice,
        discountRate = DiscountRate, vatRate = VatRate, requestLineId = RequestLineId, orderLineId = OrderLineId,
    };

    public static DocLine From(JsonObject l) => new()
    {
        VariantId = l["variantId"]?.ToString(), Sku = l["sku"]?.ToString(), Description = l["description"]?.ToString() ?? "",
        Quantity = AssetUi.Dec(l["quantity"]), UnitPrice = AssetUi.Dec(l["unitPrice"]), DiscountRate = AssetUi.Dec(l["discountRate"]),
        VatRate = l["vatRate"] == null ? 20 : AssetUi.Dec(l["vatRate"]), RequestLineId = l["requestLineId"]?.ToString(), OrderLineId = l["orderLineId"]?.ToString(),
    };
}

public sealed record ItemOption(string Id, string Label, string? Sku, decimal? PurchasePrice, decimal? SalePrice, decimal? VatRate, string Description);

public static class TradeTotals
{
    public static (decimal Subtotal, decimal Discount, decimal Vat, decimal Grand) Of(IEnumerable<DocLine> lines)
    {
        decimal sub = 0, disc = 0, vat = 0;
        foreach (var l in lines)
        {
            var gross = Math.Round(l.Quantity * l.UnitPrice, 2, MidpointRounding.AwayFromZero);
            var d = Math.Round(gross * l.DiscountRate / 100m, 2, MidpointRounding.AwayFromZero);
            var net = gross - d;
            sub += gross;
            disc += d;
            vat += Math.Round(net * l.VatRate / 100m, 2, MidpointRounding.AwayFromZero);
        }
        return (sub, disc, vat, sub - disc + vat);
    }
}
