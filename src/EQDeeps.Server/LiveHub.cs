using Microsoft.AspNetCore.SignalR;

namespace EQDeeps.Server;

/// <summary>
/// The realtime channel. Clients subscribe per session and receive:
/// "backfill" (progress + completion), "fights" (fight-list snapshots on
/// change), and "tick" (live-meter query results while combat flows).
/// </summary>
public sealed class LiveHub : Hub
{
    private readonly ClientTracker _clients;

    public LiveHub(ClientTracker clients)
    {
        _clients = clients;
    }

    public static string GroupName(string sessionId) => "session-" + sessionId;

    public Task Subscribe(string sessionId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, GroupName(sessionId));

    public Task Unsubscribe(string sessionId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(sessionId));

    public override Task OnConnectedAsync()
    {
        _clients.OnConnected();
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _clients.OnDisconnected();
        return base.OnDisconnectedAsync(exception);
    }
}
