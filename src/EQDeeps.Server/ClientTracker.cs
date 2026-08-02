namespace EQDeeps.Server;

/// <summary>
/// Counts connected UI clients (every open tab holds a hub connection). The
/// launcher ties the server's lifetime to the browser — but only to
/// *deliberate* closes: a real tab close fires pagehide and the UI beacons a
/// goodbye, while tab discarding (memory saver), freezing, and system sleep
/// just drop the socket. The exit rule therefore requires the final
/// disconnect to be paired with a goodbye; an unexplained disconnect leaves
/// the server running so the returning tab can reload and reconnect.
/// Headless runs never connect, so they are never auto-exited.
/// </summary>
public sealed class ClientTracker
{
    private int _count;
    private long _lastDisconnectTicks;
    private long _lastGoodbyeTicks;
    private volatile bool _everConnected;

    public int Count => Volatile.Read(ref _count);

    public bool EverConnected => _everConnected;

    public DateTime LastDisconnectUtc => new(Interlocked.Read(ref _lastDisconnectTicks), DateTimeKind.Utc);

    public DateTime LastGoodbyeUtc => new(Interlocked.Read(ref _lastGoodbyeTicks), DateTimeKind.Utc);

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

    /// <summary>A tab announced a genuine close (pagehide beacon).</summary>
    public void OnGoodbye()
    {
        Interlocked.Exchange(ref _lastGoodbyeTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// True when the last remaining disconnect was accompanied by a goodbye
    /// (beacon and socket-close ordering is racy, so pairing is by proximity).
    /// </summary>
    public bool LastCloseWasDeliberate =>
        Math.Abs((LastGoodbyeUtc - LastDisconnectUtc).TotalSeconds) < 15;
}
