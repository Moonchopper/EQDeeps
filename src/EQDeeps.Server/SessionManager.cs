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
    private readonly GearStore _gear;
    private readonly MobHealthStore _mobs;
    private readonly ConcurrentDictionary<string, SessionHost> _sessions = new();
    private readonly ConcurrentDictionary<string, IdentityRegistry> _registries =
        new(StringComparer.OrdinalIgnoreCase);
    private int _nextId;

    public SessionManager(
        IHubContext<LiveHub> hub,
        RecentLogs recents,
        SampleLog sample,
        GearStore gear,
        MobHealthStore mobs)
    {
        _hub = hub;
        _recents = recents;
        _sample = sample;
        _gear = gear;
        _mobs = mobs;
    }

    public SessionHost Open(OpenSessionRequest request)
    {
        if (!File.Exists(request.Path))
        {
            throw new FileNotFoundException("Log file not found.", request.Path);
        }

        var serverName = LogFileNames.TryParse(request.Path, out _, out var server) ? server : "unknown";
        var registry = _registries.GetOrAdd(serverName, _ => new IdentityRegistry());

        var session = new Session(
            request.Path,
            registry,
            new IngestOptions { BackfillFrom = request.BackfillFrom },
            emuMode: request.EmuMode);

        var id = "s" + Interlocked.Increment(ref _nextId);

        // The demo log describes a character who does not exist, so there is no
        // inventory dump to find and no gear to nudge anyone about. Its kills
        // are excluded from the mob index for the same reason: they are a
        // fixture, and letting them teach the app about a real server's mobs
        // would poison an estimate that is supposed to be evidence.
        var isSample = _sample.IsSamplePath(request.Path);
        var host = new SessionHost(
            id, session, _hub, isSample ? null : _gear, isSample ? null : _mobs);
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
