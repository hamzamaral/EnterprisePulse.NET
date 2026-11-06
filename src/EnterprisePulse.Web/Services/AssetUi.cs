using System.Globalization;
using System.Text.Json.Nodes;
using MudBlazor;

namespace EnterprisePulse.Web.Services;

/// <summary>Shared formatting of the asset screens (card, hand-over form, counts).</summary>
public static class AssetUi
{
    public static string Day(JsonNode? node) =>
        DateTime.TryParse(node?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("dd.MM.yyyy") : "—";

    public static string Stamp(JsonNode? node) =>
        DateTime.TryParse(node?.ToString(), null, DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "";

    public static decimal Dec(JsonNode? node) =>
        node != null && decimal.TryParse(node.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public static string Money(JsonNode? node, string? currency, string locale) => node == null ? "—" : Fmt.Money(Dec(node), currency, locale);

    public static string Text(JsonNode? node) => node?.ToString() is { Length: > 0 } s ? s : "—";

    public static string Today => DateTime.Today.ToString("yyyy-MM-dd");

    public static string Iso(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "";

    public static Color ConditionColor(string? condition) => condition switch
    {
        "NEW" or "GOOD" => Color.Success,
        "FAIR" => Color.Warning,
        "DAMAGED" => Color.Error,
        _ => Color.Default,
    };

    public static Color AssetStatusColor(string? status) => status switch
    {
        "IN_STOCK" => Color.Info,
        "ASSIGNED" => Color.Success,
        "IN_MAINTENANCE" => Color.Warning,
        _ => Color.Error,
    };

    public static (string Tr, string En, Color Color) Warranty(string? state) => state switch
    {
        "expired" => ("Garanti bitti", "Warranty expired", Color.Error),
        "expiring" => ("Garanti bitiyor", "Warranty ending", Color.Warning),
        "active" => ("Garanti devam ediyor", "Under warranty", Color.Success),
        _ => ("", "", Color.Default),
    };
}
