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
    private readonly ConcurrentDictionary<string, SessionHost> _sessions = new();
    private readonly ConcurrentDictionary<string, IdentityRegistry> _registries =
        new(StringComparer.OrdinalIgnoreCase);
    private int _nextId;

    public SessionManager(IHubContext<LiveHub> hub)
    {
        _hub = hub;
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
        var host = new SessionHost(id, session, _hub);
        _sessions[id] = host;
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
