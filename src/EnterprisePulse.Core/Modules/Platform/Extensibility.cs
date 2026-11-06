namespace EnterprisePulse.Core.Modules.Platform;

public sealed record DashboardCard(string Key, string Title, string Value, string Icon, string? Hint = null, string? Link = null, string Color = "primary");

public sealed record DashboardSection(string Key, string Title, List<DashboardCard> Cards);

/// <summary>A module's contribution to the home dashboard; return null when the user lacks the permission.</summary>
public interface IDashboardProvider
{
    int Order { get; }
    Task<DashboardSection?> SectionAsync();
}

/// <summary>Recurring job (reminders, SLA checks, depreciation); runs without a user, across all companies.</summary>
public interface IScheduledJob
{
    string Id { get; }
    string Cron { get; }
    Task RunAsync();
}
