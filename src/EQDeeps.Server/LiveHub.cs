using Microsoft.AspNetCore.SignalR;

namespace EQDeeps.Server;

/// <summary>
/// The realtime channel. Clients subscribe per session and receive:
/// "backfill" (progress + completion), "fights" (fight-list snapshots on
/// change), and "tick" (live-meter query results while combat flows).
/// </summary>
public sealed class LiveHub : Hub
{
    public static string GroupName(string sessionId) => "session-" + sessionId;

    public Task Subscribe(string sessionId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, GroupName(sessionId));

    public Task Unsubscribe(string sessionId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(sessionId));
}
