using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR.Client;

namespace EnterprisePulse.Web.Services;

public sealed record ApiEndpoint(string Url);

/// <summary>SignalR connection of one browser tab; raises <see cref="Notified"/> for live notifications.</summary>
public sealed class RealtimeClient(ApiEndpoint endpoint, Session session) : IAsyncDisposable
{
    private HubConnection? _hub;

    public event Func<JsonObject, Task>? Notified;

    public async Task StartAsync()
    {
        if (_hub != null) return;
        _hub = new HubConnectionBuilder()
            .WithUrl($"{endpoint.Url}/hubs/notifications", o => o.AccessTokenProvider = () => Task.FromResult(session.AccessToken))
            .WithAutomaticReconnect()
            .Build();
        _hub.On<JsonObject>("notification", async n =>
        {
            if (Notified != null) await Notified.Invoke(n);
        });
        try
        {
            await _hub.StartAsync();
        }
        catch (HttpRequestException)
        {
            // Live updates are optional; the bell still polls on navigation.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub != null) await _hub.DisposeAsync();
    }
}
