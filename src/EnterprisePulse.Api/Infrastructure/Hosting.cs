using System.Linq.Expressions;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Modules.Platform;
using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace EnterprisePulse.Api.Infrastructure;

[Authorize]
public sealed class NotificationsHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var sub = Context.User?.FindFirst("sub")?.Value;
        if (sub != null) await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{sub}");
        await base.OnConnectedAsync();
    }
}

public sealed class SignalRPusher(IHubContext<NotificationsHub> hub) : INotificationPusher
{
    public Task PushAsync(IEnumerable<Guid> userIds, string eventName, object payload) =>
        hub.Clients.Groups(userIds.Select(id => $"user:{id}").ToList()).SendAsync(eventName, payload);
}

public sealed class HangfireJobs(IBackgroundJobClient client) : IBackgroundJobs
{
    public void Enqueue<T>(Expression<Func<T, Task>> job) where T : notnull => client.Enqueue(job);
}

/// <summary>Hangfire entry point for <see cref="IScheduledJob"/>s; each run gets a fresh DI scope with no user.</summary>
public sealed class ScheduledJobRunner(IEnumerable<IScheduledJob> jobs, CurrentUser ctx)
{
    [AutomaticRetry(Attempts = 1)]
    [DisableConcurrentExecution(600)]
    public async Task RunAsync(string id)
    {
        using var _ = ctx.WithoutTenant();
        var job = jobs.FirstOrDefault(j => j.Id == id) ?? throw new InvalidOperationException($"Unknown job {id}");
        await job.RunAsync();
    }
}

/// <summary>Allows the Hangfire dashboard only from localhost in development.</summary>
public sealed class LocalDashboardFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) =>
        context.GetHttpContext().Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip);
}
