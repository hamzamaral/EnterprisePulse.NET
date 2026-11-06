using MudBlazor;

namespace EnterprisePulse.Web.Services;

/// <summary>Shared labels and formatting of the HR screens (leave, attendance).</summary>
public static class HrUi
{
    private static readonly Dictionary<string, (string Tr, string En, string Short, Color Color)> Days = new()
    {
        ["PRESENT"] = ("Geldi", "Present", "✓", Color.Success),
        ["LATE"] = ("Geç geldi", "Late", "G", Color.Warning),
        ["ABSENT"] = ("Gelmedi", "Absent", "×", Color.Error),
        ["INCOMPLETE"] = ("Çıkış yok", "No check-out", "?", Color.Warning),
        ["LEAVE"] = ("İzinli", "On leave", "İ", Color.Info),
        ["HOLIDAY"] = ("Resmi tatil", "Holiday", "T", Color.Secondary),
        ["OFF"] = ("Tatil günü", "Day off", "–", Color.Default),
        ["UPCOMING"] = ("Planlı", "Upcoming", "", Color.Default),
    };

    public static IEnumerable<string> DayStatuses => Days.Keys;

    public static string DayLabel(string? status, string locale) =>
        status != null && Days.TryGetValue(status, out var d) ? (locale == "en" ? d.En : d.Tr) : status ?? "";

    public static string DayShort(string? status) => status != null && Days.TryGetValue(status, out var d) ? d.Short : "";

    public static Color DayColor(string? status) => status != null && Days.TryGetValue(status, out var d) ? d.Color : Color.Default;

    public static string DayClass(string? status) => "ep-day ep-day-" + (status ?? "OFF").ToLowerInvariant();

    public static int Int(System.Text.Json.Nodes.JsonNode? node) => node?.GetValue<int>() ?? 0;

    public static bool Bool(System.Text.Json.Nodes.JsonNode? node) => node?.GetValue<bool>() ?? false;

    public static string Minutes(int minutes) => minutes <= 0 ? "0:00" : $"{minutes / 60}:{minutes % 60:00}";

    public static string MonthOf(DateTime d) => d.ToString("yyyy-MM");
}
