using System.Collections.Concurrent;
using EQDeeps.Core.Ingestion;
using EQDeeps.Core.Sessions;
using Microsoft.AspNetCore.SignalR;

namespace EQDeeps.Server;

/// <summary>
/// Opens, tracks, and closes sessions. Identity registries are shared per game
/// server (characters on one server see the same world), created on demand.
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly IHubContext<LiveHub> _hub;
    private readonly RecentLogs _recents;
    private readonly SampleLog _sample;
    private readonly MobHealthStore _mobs;
    private readonly MobAttackStore _attacks;
    private readonly LogCacheStore _caches;
    private readonly ItemStore _items;
    private readonly ConcurrentDictionary<string, SessionHost> _sessions = new();
    private readonly ConcurrentDictionary<string, IdentityRegistry> _registries =
        new(StringComparer.OrdinalIgnoreCase);
    private int _nextId;

    public SessionManager(
        IHubContext<LiveHub> hub,
        RecentLogs recents,
        SampleLog sample,
        MobHealthStore mobs,
        MobAttackStore attacks,
        LogCacheStore caches,
        ItemStore items)
    {
        _hub = hub;
        _recents = recents;
        _sample = sample;
        _mobs = mobs;
        _attacks = attacks;
        _caches = caches;
        _items = items;

        // Caches for logs that are gone, or that nobody has opened in months,
        // are reclaimed here rather than never. Off the request path: it
        // reads a header per file, and there is no reason to make the first
        // open wait for it.
        _ = Task.Run(() => _caches.Sweep());
    }

    public SessionHost Open(OpenSessionRequest request)
    {
        if (!File.Exists(request.Path))
        {
            throw new FileNotFoundException("Log file not found.", request.Path);
        }

        var serverName = LogFileNames.TryParse(request.Path, out _, out var server) ? server : "unknown";
        var registry = _registries.GetOrAdd(serverName, _ => new IdentityRegistry());

        // The cache is validated against the log right here, so what the
        // session restores is what the file still holds. Null when there can
        // be no cache; the session then reads the log as it always has.
        var session = new Session(
            request.Path,
            registry,
            new IngestOptions { BackfillFrom = request.BackfillFrom },
            emuMode: request.EmuMode,
            cache: _caches.Open(request.Path, request.EmuMode));

        var id = "s" + Interlocked.Increment(ref _nextId);

        var isSample = _sample.IsSamplePath(request.Path);
        // The demo log's kills and the swings it takes are excluded from the
        // mob indexes: they are a fixture, and letting them teach the app about
        // a real server's mobs would poison an estimate that is supposed to be
        // evidence.
        var host = new SessionHost(
            id, session, _hub, isSample ? null : _mobs, isSample ? null : _attacks, isSample ? null : _items);
        _sessions[id] = host;
        if (!isSample)
        {
            // The demo log is always offered by the sample entry itself; letting
            // it into the MRU would make it look like a log the player uses.
            _recents.Touch(Path.GetFullPath(request.Path));
        }

        return host;
    }

    public SessionHost? Get(string id) => _sessions.GetValueOrDefault(id);

    public List<SessionInfo> List() => _sessions.Values
        .OrderBy(h => h.Id, StringComparer.Ordinal)
        .Select(h => h.Info())
        .ToList();

    public async Task<bool> CloseAsync(string id)
    {
        if (!_sessions.TryRemove(id, out var host))
        {
            return false;
        }

        await host.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList())
        {
            await CloseAsync(id).ConfigureAwait(false);
        }
    }
}
