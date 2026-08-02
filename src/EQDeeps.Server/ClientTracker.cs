namespace EQDeeps.Server;

/// <summary>
/// Counts connected UI clients (every open tab holds a hub connection). The
/// launcher uses this to tie the server's lifetime to the browser: once a tab
/// has connected and the last one has been gone past a grace period, the app
/// exits — nothing is lost, since reopening backfills from the log. Headless
/// runs never connect, so they are never auto-exited.
/// </summary>
public sealed class ClientTracker
{
    private int _count;
    private long _lastDisconnectTicks;
    private volatile bool _everConnected;

    public int Count => Volatile.Read(ref _count);

    public bool EverConnected => _everConnected;

    public DateTime LastDisconnectUtc => new(Interlocked.Read(ref _lastDisconnectTicks), DateTimeKind.Utc);

    public void OnConnected()
    {
        Interlocked.Increment(ref _count);
        _everConnected = true;
    }

    public void OnDisconnected()
    {
        if (Interlocked.Decrement(ref _count) <= 0)
        {
            Interlocked.Exchange(ref _lastDisconnectTicks, DateTime.UtcNow.Ticks);
        }
    }
}
