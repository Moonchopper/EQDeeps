using EQDeeps.Core.Gear;
using EQDeeps.Core.Ingestion;
using EQDeeps.Core.Mobs;
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

    /// <summary>
    /// How far back a kill sweep reaches beyond the last one. Fights close on
    /// a timeout as well as on a death, so one can be finalized a little after
    /// a later fight already has been; a small overlap catches that, and the
    /// index ignores kills it already holds.
    /// </summary>
    private static readonly TimeSpan HarvestOverlap = TimeSpan.FromSeconds(120);

    private readonly IHubContext<LiveHub> _hub;
    private readonly GearStore? _gear;
    private readonly MobHealthStore? _mobs;
    private readonly MobAttackStore? _attacks;
    private readonly GearWatcher? _watcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly string _group;
    private readonly Task _runTask;
    private readonly Task _pushTask;
    private readonly Task _expiryTask;
    private readonly Task? _gearTask;

    private ContextTimeline? _context;
    private int _contextVersion = -1;
    private DefenderLevels? _levels;
    private int _levelsVersion = -1;

    private volatile bool _liveDirty;
    private volatile bool _gearDirty;
    private long _backfillBytes = -1;
    private long _backfillTotal;
    private bool _backfillCompleteSent;
    private int _lastPushedFightVersion = -1;
    private DateTime _harvested = DateTime.MinValue;
    private DateTime _attacksHarvested = DateTime.MinValue;

    /// <summary>
    /// Learned health by mob key, rebuilt only when a sweep actually banked a
    /// new kill. The fight list is rebuilt on every push — up to 20 times a
    /// second — and re-deriving quantiles over every sample that often would
    /// cost far more than the column is worth. Swapped whole, so a reader
    /// either sees the old map or the new one.
    /// </summary>
    private volatile IReadOnlyDictionary<string, MobHealthEstimate>? _health;

    public SessionHost(
        string id,
        Session session,
        IHubContext<LiveHub> hub,
        GearStore? gear = null,
        MobHealthStore? mobs = null,
        MobAttackStore? attacks = null)
    {
        Id = id;
        Session = session;
        Engine = new QueryEngine(session);
        _hub = hub;
        _gear = gear;
        _mobs = mobs;
        _attacks = attacks;
        _group = LiveHub.GroupName(id);

        if (gear is not null)
        {
            _watcher = new GearWatcher(session.Character, session.Server, session.Path, gear);
        }

        // Whatever past sessions learned is available from the first frame the
        // client draws, which is the point of persisting it at all.
        _health = mobs?.Lookup(session.Server);

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
                Session.UnrecognizedLines, Session.Ingestion.MalformedLines,
                Session.StanceSwitches);
        }
    }

    public List<FightInfo> Fights()
    {
        lock (Session.Gate)
        {
            return FightInfo.Build(
                Session.Fights.Fights, Session.Character, Session.Identity, _health);
        }
    }

    /// <summary>
    /// What this server's mobs are worth (F25). Server-wide rather than
    /// session-wide: the evidence is about the world, so every log opened
    /// against this server has contributed to it and reads the same answer.
    /// </summary>
    public MobHealthReport MobHealth()
    {
        var estimates = _mobs?.Estimates(Session.Server) ?? [];
        return new MobHealthReport(
            Session.Server,
            estimates,
            estimates.Sum(e => e.Samples),
            estimates.Any(e => e.Difficulty is not null));
    }

    public QueryResult Execute(QuerySpec spec)
    {
        lock (Session.Gate)
        {
            return Engine.Execute(spec);
        }
    }

    /// <summary>
    /// Zone and level spans for the chart strip. Rebuilt only when records
    /// have arrived: it is a walk of the whole stream, and the answer cannot
    /// change while the stream does not.
    /// </summary>
    public ContextTimeline Context()
    {
        lock (Session.Gate)
        {
            if (_context is null || _contextVersion != Session.Records.Version)
            {
                _context = ContextTimeline.Build(Session.Records, Session.Character);
                _contextVersion = Session.Records.Version;
            }

            return _context;
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
    /// What this server's mobs do to the people in front of them (F26). Like
    /// mob health this is the server's answer rather than the session's, but
    /// unlike it the rows are per defender level — how hard something hits is a
    /// fact about a pairing, not about the mob.
    /// </summary>
    public MobAttackReport MobAttacks()
    {
        var estimates = _attacks?.Estimates(Session.Server) ?? [];

        // The character's level right now decides which rows the panel opens
        // on: a level-58 reading a level-40's numbers would be reading someone
        // else's fight. Null when the log has not said yet, which the panel
        // reports rather than papering over.
        int? level;
        lock (Session.Gate)
        {
            level = Session.Records.Count == 0
                ? null
                : LevelsLocked().LevelOf(
                    Session.Character, Session.Records[Session.Records.Count - 1].Timestamp);
        }

        return new MobAttackReport(
            Session.Server,
            Session.Character,
            level,
            estimates,
            estimates.Sum(e => e.Landed),
            estimates.Any(e => e.Difficulty is not null));
    }

    /// <summary>The tail of the incoming-damage stream over a scope (F26).</summary>
    public IncomingHitsResult IncomingHits(IncomingHitsRequest request)
    {
        lock (Session.Gate)
        {
            return IncomingHitsBuilder.Build(
                Session.Records,
                Session.Fights,
                Session.Identity,
                request.Scope,
                request.Limit ?? IncomingHitsBuilder.DefaultLimit,
                request.OwnerOnly ? [Session.Character] : request.Defenders);
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
                        fightsPayload = new
                        {
                            sessionId = Id,
                            fights = FightInfo.Build(
                                Session.Fights.Fights, Session.Character, Session.Identity,
                                _health),
                        };
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

    /// <summary>
    /// Sweeps finished kills into the server's mob index and refreshes the
    /// lookup the fight list reads. Returns whether anything was new — which
    /// is rare after the first sweep, since a sweep re-offers the same kills
    /// and the index recognizes them.
    /// </summary>
    private bool HarvestKills()
    {
        if (_mobs is null)
        {
            return false;
        }

        // The first sweep has no watermark to reach back from, and DateTime
        // cannot go earlier than its own minimum.
        var since = _harvested == DateTime.MinValue
            ? DateTime.MinValue
            : _harvested - HarvestOverlap;

        List<KillSample> samples;
        lock (Session.Gate)
        {
            samples = MobHealthIndex.Harvest(Session.Fights.Fights, since);
        }

        if (samples.Count == 0)
        {
            return false;
        }

        foreach (var sample in samples)
        {
            if (sample.KilledAt > _harvested)
            {
                _harvested = sample.KilledAt;
            }
        }

        if (_mobs.Record(Session.Server, samples) == 0)
        {
            return false;
        }

        _health = _mobs.Lookup(Session.Server);
        return true;
    }

    /// <summary>
    /// Sweeps closed fights' incoming damage into the server's attack index
    /// (F26). Rides the same tick as <see cref="HarvestKills"/> and for the
    /// same reason — a fight is only final once the timeouts have had their
    /// say — but reads a different thing out of it: every swing the mob threw,
    /// not the one line that said it died.
    ///
    /// <para>Nothing is pushed when this banks something. The profiles are
    /// analysis rather than a live readout, and the panel that shows them
    /// refetches on the time frame like the Mobs tab does.</para>
    /// </summary>
    private void HarvestAttacks()
    {
        if (_attacks is null)
        {
            return;
        }

        var since = _attacksHarvested == DateTime.MinValue
            ? DateTime.MinValue
            : _attacksHarvested - HarvestOverlap;

        List<AttackSample> samples;
        lock (Session.Gate)
        {
            samples = MobAttackIndex.Harvest(
                Session.Records,
                Session.Fights.Fights,
                Session.Identity,
                LevelsLocked(),
                since);
        }

        if (samples.Count == 0)
        {
            return;
        }

        foreach (var sample in samples)
        {
            if (sample.FightEnd > _attacksHarvested)
            {
                _attacksHarvested = sample.FightEnd;
            }
        }

        _attacks.Record(Session.Server, samples);
    }

    /// <summary>
    /// Every level the log states, cached against the record version. Rebuilt
    /// only when records have arrived: it is a walk of the whole stream and the
    /// expiry tick asks for it once a second, which on a multi-gigabyte log is
    /// the difference between free and the most expensive thing the server
    /// does. Callers must hold <see cref="Sessions.Session.Gate"/>.
    /// </summary>
    private DefenderLevels LevelsLocked()
    {
        if (_levels is null || _levelsVersion != Session.Records.Version)
        {
            _levels = DefenderLevels.Build(Session.Records, Session.Character);
            _levelsVersion = Session.Records.Version;
        }

        return _levels;
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

                // Kills are banked here rather than at the moment a fight
                // closes, because what makes a kill usable is that its fight is
                // final — and a fight is only final once the timeouts have had
                // their say. Riding the expiry tick means that has just
                // happened.
                if (HarvestKills())
                {
                    changed = true;
                }

                // Rides the same tick for the same reason, but never sets
                // `changed`: the fight list does not read attack profiles, so
                // banking one is not a reason to push the whole thing again.
                HarvestAttacks();

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
