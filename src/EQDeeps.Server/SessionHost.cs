using EQDeeps.Core.Gear;
using EQDeeps.Core.Ingestion;
using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;
using Microsoft.AspNetCore.SignalR;

namespace EQDeeps.Server;

/// <summary>
/// Runs one session and its realtime push plumbing: a coalescing loop that
/// wakes on processed batches, waits ~50 ms to batch bursts, then pushes fight
/// snapshots, backfill progress, and live-meter ticks to the session's SignalR
/// group. A 1 Hz wall-clock timer expires idle fights after backfill so the
/// meter closes fights between log lines. All session state reads happen under
/// the session gate.
/// </summary>
public sealed class SessionHost : IAsyncDisposable
{
    private const int CoalesceDelayMs = 50;

    /// <summary>
    /// The inventory dump is a manual act; noticing it within a few seconds is
    /// as responsive as it needs to be, and a stat that often costs nothing.
    /// </summary>
    private static readonly TimeSpan GearPollInterval = TimeSpan.FromSeconds(5);

    private readonly IHubContext<LiveHub> _hub;
    private readonly GearStore? _gear;
    private readonly GearWatcher? _watcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly string _group;
    private readonly Task _runTask;
    private readonly Task _pushTask;
    private readonly Task _expiryTask;
    private readonly Task? _gearTask;

    private volatile bool _liveDirty;
    private volatile bool _gearDirty;
    private long _backfillBytes = -1;
    private long _backfillTotal;
    private bool _backfillCompleteSent;
    private int _lastPushedFightVersion = -1;

    public SessionHost(string id, Session session, IHubContext<LiveHub> hub, GearStore? gear = null)
    {
        Id = id;
        Session = session;
        Engine = new QueryEngine(session);
        _hub = hub;
        _gear = gear;
        _group = LiveHub.GroupName(id);

        if (gear is not null)
        {
            _watcher = new GearWatcher(session.Character, session.Server, session.Path, gear);
        }

        session.BatchProcessed += OnBatchProcessed;
        _runTask = Task.Run(() => session.RunAsync(_cts.Token));
        _pushTask = Task.Run(() => PushLoopAsync(_cts.Token));
        _expiryTask = Task.Run(() => ExpiryLoopAsync(_cts.Token));
        _gearTask = _watcher is null ? null : Task.Run(() => GearLoopAsync(_cts.Token));
    }

    public string Id { get; }

    public Session Session { get; }

    public QueryEngine Engine { get; }

    /// <summary>Faulted when ingestion failed fatally (file missing etc.).</summary>
    public Task RunTask => _runTask;

    public SessionInfo Info()
    {
        lock (Session.Gate)
        {
            return new SessionInfo(
                Id, Session.Path, Session.Character, Session.Server,
                Session.BackfillComplete, Session.Records.Count, Session.Fights.Fights.Count,
                Session.UnrecognizedLines, Session.Ingestion.MalformedLines);
        }
    }

    public List<FightInfo> Fights()
    {
        lock (Session.Gate)
        {
            return FightInfo.Build(Session.Fights.Fights);
        }
    }

    public QueryResult Execute(QuerySpec spec)
    {
        lock (Session.Gate)
        {
            return Engine.Execute(spec);
        }
    }

    public TimelineResult Timeline(TimelineRequest request)
    {
        lock (Session.Gate)
        {
            return TimelineBuilder.Build(
                Session.Records, Session.Fights, Session.Character, request.Scope);
        }
    }

    /// <summary>
    /// Everything known about this character's gear: the snapshots, the changes
    /// between them, and how much combat has happened since the last one was
    /// proven.
    /// </summary>
    public GearReport Gear()
    {
        var snapshots = _gear?.List(Session.Character, Session.Server) ?? [];
        var newest = snapshots.Count > 0 ? snapshots[^1] : null;

        int fightsSince;
        lock (Session.Gate)
        {
            fightsSince = newest is null
                ? Session.Fights.Fights.Count
                : Session.Fights.Fights.Count(f => f.LastDamageTime > newest.CapturedAt);
        }

        return new GearReport(
            snapshots,
            GearHistory.Changes(snapshots),
            new GearStatus(
                newest is not null,
                newest?.CapturedAt,
                fightsSince,
                _watcher?.ExpectedPath ?? string.Empty,
                GearWatcher.Command));
    }

    private void OnBatchProcessed(LogBatch batch)
    {
        if (batch.Phase == IngestPhase.Live && batch.Entries.Count > 0)
        {
            _liveDirty = true;
        }

        Interlocked.Exchange(ref _backfillBytes, batch.BytesProcessed);
        Interlocked.Exchange(ref _backfillTotal, batch.TotalBytes ?? 0);
        Notify();
    }

    private void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A push is already pending; it will pick up the latest state.
        }
    }

    private async Task PushLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                await Task.Delay(CoalesceDelayMs, ct).ConfigureAwait(false);

                object? fightsPayload = null;
                object? tickPayload = null;
                object? backfillPayload = null;
                object? gearPayload = null;

                if (_gearDirty)
                {
                    _gearDirty = false;
                    gearPayload = new { sessionId = Id, gear = Gear() };
                }

                lock (Session.Gate)
                {
                    if (Session.Fights.Version != _lastPushedFightVersion)
                    {
                        _lastPushedFightVersion = Session.Fights.Version;
                        fightsPayload = new { sessionId = Id, fights = FightInfo.Build(Session.Fights.Fights) };
                    }

                    if (_liveDirty)
                    {
                        _liveDirty = false;
                        tickPayload = BuildTickLocked();
                    }

                    var bytes = Interlocked.Read(ref _backfillBytes);
                    if (bytes >= 0 && (!Session.BackfillComplete || !_backfillCompleteSent))
                    {
                        _backfillCompleteSent = Session.BackfillComplete;
                        backfillPayload = new
                        {
                            sessionId = Id,
                            bytesProcessed = bytes,
                            totalBytes = Interlocked.Read(ref _backfillTotal),
                            complete = Session.BackfillComplete,
                        };
                    }
                }

                if (backfillPayload is not null)
                {
                    await _hub.Clients.Group(_group).SendAsync("backfill", backfillPayload, ct).ConfigureAwait(false);
                }

                if (fightsPayload is not null)
                {
                    await _hub.Clients.Group(_group).SendAsync("fights", fightsPayload, ct).ConfigureAwait(false);
                }

                if (tickPayload is not null)
                {
                    await _hub.Clients.Group(_group).SendAsync("tick", tickPayload, ct).ConfigureAwait(false);
                }

                if (gearPayload is not null)
                {
                    await _hub.Clients.Group(_group).SendAsync("gear", gearPayload, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private object? BuildTickLocked()
    {
        // Live meter: damage summary over the in-progress fights (or the most
        // recent fight when everything just closed).
        var fightIds = Session.Fights.ActiveFights.Select(f => f.Id).OrderBy(id => id).ToList();
        if (fightIds.Count == 0)
        {
            var last = Session.Fights.Fights.LastOrDefault();
            if (last is null)
            {
                return null;
            }

            fightIds = [last.Id];
        }

        var result = Engine.Execute(new QuerySpec
        {
            Source = QuerySource.Damage,
            Scope = new QueryScope { FightIds = fightIds },
            GroupBy = [Dimension.Player],
            Metrics = ["total", "dps", "sdps", "percentOfTotal"],
        });

        return new { sessionId = Id, fightIds, result };
    }

    /// <summary>
    /// Polls for a new inventory dump. Deliberately independent of backfill:
    /// the dump on disk describes gear now, and a player opening a large log
    /// should not wait for it to finish before their gear appears.
    /// </summary>
    private async Task GearLoopAsync(CancellationToken ct)
    {
        try
        {
            // An immediate look, so a dump written before the app started is
            // picked up at once rather than after the first interval.
            if (_watcher!.Poll())
            {
                _gearDirty = true;
                Notify();
            }

            using var timer = new PeriodicTimer(GearPollInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_watcher.Poll())
                {
                    _gearDirty = true;
                    Notify();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ExpiryLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!Session.BackfillComplete)
                {
                    continue;
                }

                bool changed;
                lock (Session.Gate)
                {
                    var before = Session.Fights.Version;
                    Session.Fights.ApplyPendingCorrections();

                    // Log timestamps are local time, so wall-clock local time is
                    // the right "now" for closing idle fights between lines.
                    Session.Fights.ExpireFights(DateTime.Now);
                    changed = Session.Fights.Version != before;
                }

                if (changed)
                {
                    Notify();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Session.BatchProcessed -= OnBatchProcessed;
        _cts.Cancel();
        foreach (var task in new[] { _runTask, _pushTask, _expiryTask, _gearTask }.OfType<Task>())
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
                // Ingestion may fault if the file vanished mid-shutdown.
            }
        }

        _cts.Dispose();
        _signal.Dispose();
    }
}
