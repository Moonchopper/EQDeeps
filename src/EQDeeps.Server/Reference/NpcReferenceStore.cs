using System.Text.Json;
using EQDeeps.Core.Achievements;
using EQDeeps.Core.Maps;
using EQDeeps.Core.Reference;

namespace EQDeeps.Server.Reference;

/// <summary>What the reference layer can currently answer, and why not when it cannot.</summary>
/// <param name="SnapshotUtc">When the shipped snapshot was taken; null with no snapshot (ADR-020 Decision 1, reversed 2026-09-21).</param>
/// <param name="RefreshedUtc">The newest on-disk cache write newer than the snapshot — what "refreshed 3 days ago" means on screen; null when there has been none.</param>
public sealed record ReferenceStatus(
    bool Available,
    string Source,
    string HomeUrl,
    int Names,
    int Listings,
    string? Error,
    DateTime? SnapshotUtc,
    DateTime? RefreshedUtc,
    RefreshStatus Refresh);

/// <summary>Where a player-pressed Refresh stands (ADR-020 Decision 1's amendment): the only path this store has to the network.</summary>
public sealed record RefreshStatus(bool Running, int FilesTotal, int FilesChecked, int FilesChanged, DateTime? FinishedUtc, string? Error);

/// <summary>
/// Where the atlas walk stands (F35, ADR-023 Decision 7): a background read of every shard the
/// index implies, so the hunting panel can say "zone 12 of 79" while it runs and the hunt endpoint
/// can rank whatever has landed so far. <see cref="Complete"/> means the walk finished, not that
/// every shard succeeded — a flaky zone is recorded in <see cref="Error"/> and skipped, never
/// stalls the rest.
/// </summary>
public sealed record AtlasStatus(bool Enabled, int ZonesTotal, int ZonesRead, bool Running, bool Complete,
    string Source, string HomeUrl, string? Error);

/// <summary>One NPC of a zone's roster: enough to list it, place it on the map, and open it.</summary>
public sealed record ZoneRosterNpc(
    int Id,
    string Name,
    int? Level,
    int? MaxLevel,
    int SpawnPoints,
    IReadOnlyList<double[]> Locations);

/// <summary>
/// Every NPC the site lists in one zone. <see cref="Known"/> false means the
/// question could not be answered — the zone has no id in the table, or the
/// file at its id turned out to hold some other zone — as opposed to a zone
/// the site simply lists nothing in.
/// </summary>
public sealed record ZoneRoster(
    string ShortName,
    string? ZoneName,
    bool Known,
    IReadOnlyList<ZoneRosterNpc> Npcs);

/// <summary>
/// The NPC reference (F30, ADR-020): a name index and the stat blocks behind
/// it, fetched from <see cref="IReferenceSource"/> on demand and cached on
/// this machine.
///
/// <para><b>Nothing is fetched until someone asks.</b> There is no background
/// refresh and no fetch at start-up: the first request for a search or a stat
/// block is what reaches out, which is what makes the Settings switch in the
/// UI a real one — leave the Bestiary closed and the app never speaks to
/// anybody. The index is revalidated at most once a day, with an ETag, so the
/// usual cost of a session is a 304 and no bytes.</para>
///
/// <para><b>Recomputable, and never load-bearing.</b> Everything here is
/// someone else's data about a game; the parser, the fights and every measured
/// number stand entirely without it. A failed fetch, a corrupt cache or a
/// changed shape leaves the app exactly as it was and is reported as
/// <see cref="ReferenceStatus.Error"/> rather than thrown — which is also why
/// the cache is under <c>reference\</c> with its own redirect flag and can be
/// deleted at any time.</para>
///
/// <para><b>With a shipped snapshot (ADR-020 Decision 1, reversed 2026-09-21):</b> every read here —
/// the index, a shard, a roster, the atlas walk — answers from memory, then a cache file only when a
/// Refresh actually wrote it after the snapshot was taken, then the snapshot itself, and reaches the
/// network for none of it. <see cref="StartRefresh"/>, the player's own button, is the only path
/// upstream from this point on; everything above this paragraph describes the store's behaviour with
/// no snapshot, which is unchanged and still exactly what every pre-existing test here exercises.</para>
/// </summary>
public sealed class NpcReferenceStore
{
    /// <summary>How stale the index may get before a conditional GET is worth it.</summary>
    private static readonly TimeSpan IndexMaxAge = TimeSpan.FromDays(1);

    /// <summary>
    /// How stale a shard file may get before a conditional GET is worth it — weekly rather than
    /// the index's daily check (F35, ADR-023 Decision 7): 79 conditional GETs a day for data that
    /// only moves with a patch would be rudeness, not freshness.
    /// </summary>
    private static readonly TimeSpan ShardMaxAge = TimeSpan.FromDays(7);

    private readonly IReferenceSource _source;
    private readonly IReferenceSnapshot _snapshot;
    private readonly string _root;
    private readonly bool _enabled;
    private readonly TimeSpan _atlasPause;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, IReadOnlyDictionary<int, NpcDetail>> _shards = [];

    private NpcIndex? _index;
    private DateTime? _refreshedUtc;
    private bool _revalidated;
    private string? _error;

    // The atlas walk's own state (F35, ADR-023 Decision 7). Kept apart from the index/shard
    // _error above: a shard revalidation blip and a walk-wide problem are different questions, and
    // conflating them would have the Bestiary's own errors bleed into the hunting panel's status.
    private int _atlasStarted;
    private Task? _atlasTask;
    private volatile int _atlasZonesTotal;
    private volatile int _atlasZonesRead;
    private volatile bool _atlasComplete;
    private string? _atlasError;
    private SlayerAtlas? _atlas;
    private int _atlasBuiltFrom = -1;

    // Refresh's own state — the only path to the network once a snapshot ships. _refreshStarted
    // doubles as the CAS guard and the "running" flag: unlike the atlas walk, which runs once ever
    // per store, a Refresh is a button the player can press again once the last one finished, so
    // this resets to 0 in the walk's own finally block rather than staying 1 forever.
    private int _refreshStarted;
    private Task? _refreshTask;
    private volatile int _refreshFilesTotal;
    private volatile int _refreshFilesChecked;
    private volatile int _refreshFilesChanged;
    private DateTime? _refreshFinishedUtc;
    private string? _refreshError;

    public NpcReferenceStore(
        IReferenceSource source, string? root = null, bool enabled = true, TimeSpan? atlasPause = null,
        IReferenceSnapshot? snapshot = null)
    {
        _source = source;
        _enabled = enabled;
        _atlasPause = atlasPause ?? TimeSpan.FromSeconds(1.5);
        _snapshot = snapshot ?? new NoReferenceSnapshot();
        _root = Path.Combine(
            root ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQDeeps"),
            "reference");
    }

    public string SourceName => _source.Name;

    public string NpcUrl(int id) => _source.NpcUrl(id);

    public ReferenceStatus Status() => new(
        _index is not null,
        _source.Name,
        _source.HomeUrl,
        _index?.NameCount ?? 0,
        _index?.EntryCount ?? 0,
        _enabled ? _error : "reference lookups are switched off (--no-reference)",
        _snapshot.SnapshotUtc,
        DiskRefreshedUtc(),
        new RefreshStatus(_refreshStarted == 1, _refreshFilesTotal, _refreshFilesChecked, _refreshFilesChanged, _refreshFinishedUtc, _refreshError));

    /// <summary>
    /// Where the walk stands right now. <see cref="AtlasStatus.ZonesTotal"/> reads 0 until the walk
    /// has read the index for itself — nobody else's index read counts, because "how many shards"
    /// is a question only the walk needs an answer to.
    /// </summary>
    public AtlasStatus AtlasStatus() => new(
        _enabled,
        _atlasZonesTotal,
        _atlasZonesRead,
        _atlasStarted == 1 && !_atlasComplete,
        _atlasComplete,
        _source.Name,
        _source.HomeUrl,
        _atlasError);

    /// <summary>
    /// Starts the walk, once. <b>This POST is the ask</b> (ADR-020 Decision 2, as ADR-023 Decision 7
    /// restates it for the bulk read): nothing else in the app ever calls this, so a hunting panel
    /// nobody has opened keeps every one of the ~79 shards unread. Idempotent by a compare-and-swap
    /// rather than a timing assumption, so two requests landing back to back — the panel opening
    /// twice, a double click — can never start two walks at once (Gotcha 2).
    /// </summary>
    public AtlasStatus StartAtlas()
    {
        if (_enabled && Interlocked.CompareExchange(ref _atlasStarted, 1, 0) == 0)
        {
            _atlasTask = Task.Run(WalkAsync);
        }

        return AtlasStatus();
    }

    /// <summary>
    /// Test seam: the walk's own background task, so a test can await its completion deterministically
    /// instead of polling <see cref="AtlasStatus"/> in a loop. Internal rather than public — reached
    /// only through <c>InternalsVisibleTo</c> — because nothing outside a test needs to await a
    /// fire-and-forget background walk; the public contract is <see cref="AtlasStatus"/> alone.
    /// </summary>
    internal Task? AtlasTask => _atlasTask;

    /// <summary>
    /// Every shard the index implies, read one at a time (F35, ADR-023 Decision 7) — a user reading
    /// the site's own zone pages would pull the same ~79 files one click at a time, and the app
    /// should not pull them faster than a person could. Paced by <see cref="_atlasPause"/>, and only
    /// after a shard that actually reached the network: one served from memory or a fresh disk copy
    /// costs nothing and is not what the pacing exists to slow down. A shard that fails is recorded
    /// in <see cref="_atlasError"/> and skipped; the rest of the walk still runs, because one flaky
    /// zone must not silently narrow every hunt built on top of the atlas. Never throws (Gotcha 1):
    /// nothing awaits this task, so an exception here would otherwise vanish into an unobserved
    /// fault.
    ///
    /// <para>With a snapshot shipped, every <see cref="ShardAsync"/> call below answers from memory,
    /// cache or the bundle and never reaches the network (<see cref="ShardLoad.Network"/> stays
    /// false throughout), so this walk runs to completion with no pause and no request at all — the
    /// pacing exists for the network case, and there is none to pace (ADR-020 Decision 1's
    /// amendment).</para>
    /// </summary>
    private async Task WalkAsync()
    {
        try
        {
            var index = await IndexAsync().ConfigureAwait(false);
            var shards = ShardsOf(index);

            _atlasZonesTotal = shards.Count;

            for (var i = 0; i < shards.Count; i++)
            {
                ShardLoad load;
                try
                {
                    load = await ShardAsync(shards[i], CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // Belt and braces: ShardAsync's own IO already swallows what it can name, but
                    // the walk's invariant is stronger than that method's — it must never throw at
                    // all, because nothing is awaiting this background task to observe a fault.
                    load = new ShardLoad(null, false, e.Message);
                }

                _atlasZonesRead++;
                if (load.Error is not null)
                {
                    _atlasError = load.Error;
                }

                // A pause "between them", not after the last one — the final shard's completion
                // must not wait on a gap nobody is going to see the other side of.
                if (load.Network && i < shards.Count - 1)
                {
                    await Task.Delay(_atlasPause).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _atlasComplete = true;
        }
    }

    /// <summary>Every shard the index implies, in ascending order — shared by the atlas walk and Refresh, both of which read exactly the set of zones the index currently names.</summary>
    private static List<int> ShardsOf(NpcIndex? index) =>
        index is null
            ? []
            : index.Entries.Select(e => NpcReferenceFormat.ShardOf(e.Id)).Distinct().OrderBy(s => s).ToList();

    /// <summary>
    /// The atlas built from whatever shards are loaded right now (F35, ADR-023 Decision 7) — never
    /// a fetch of its own; that is what the walk above and the POST that starts it are for. Derived,
    /// never stored (Decision 7 again), and memoised on how many shards are loaded so a client
    /// polling this every couple of seconds while the walk runs does not re-derive the whole world
    /// on a call where nothing new has landed since the last one.
    /// </summary>
    public async Task<SlayerAtlas> AtlasAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_atlas is null || _atlasBuiltFrom != _shards.Count)
            {
                _atlas = SlayerAtlas.Build(_shards.Values.SelectMany(s => s.Values), ZoneTable.Default);
                _atlasBuiltFrom = _shards.Count;
            }

            return _atlas;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The name index, from memory, then disk, then the network. Null when it
    /// cannot be had — offline on a first run, or switched off.
    /// </summary>
    public async Task<NpcIndex?> IndexAsync(CancellationToken ct = default)
    {
        if (!_enabled)
        {
            return null;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = CachePath("search-index.json");
            var hadIndex = _index is not null;
            EnsureIndexLoaded();
            if (!hadIndex && _index is not null && CacheIsAuthoritative(path))
            {
                _refreshedUtc = File.GetLastWriteTimeUtc(path);
            }

            // A snapshot shipped: memory, then a cache file only when it is newer than the
            // snapshot, then the snapshot itself — all local, all above. Nothing past this point
            // ever reaches the network; only StartRefresh does (ADR-020 Decision 1's amendment).
            // The daily revalidation below is a no-snapshot idea — the whole reason the snapshot
            // exists is so an install does not need it.
            if (_snapshot.SnapshotUtc is not null)
            {
                return _index;
            }

            // One revalidation per run, and only when the copy is a day old:
            // the data changes when the site's author edits it, not by the
            // minute, and a parse is not worth interrupting for.
            var stale = _refreshedUtc is null || DateTime.UtcNow - _refreshedUtc > IndexMaxAge;
            if (!_revalidated && (stale || _index is null))
            {
                _revalidated = true;
                var fetch = await _source
                    .GetAsync(NpcReferenceFormat.IndexPath, EtagFor("search-index.json"), ct)
                    .ConfigureAwait(false);
                if (fetch.Failed)
                {
                    _error = fetch.Error;
                }
                else if (fetch.Modified && fetch.Content is { Length: > 0 })
                {
                    var entries = NpcReferenceFormat.ParseIndex(fetch.Content);
                    if (entries.Count > 0)
                    {
                        _index = new NpcIndex(entries, NpcReferenceFormat.ParseIndexZones(fetch.Content));
                        Write(path, fetch.Content, "search-index.json", fetch.ETag);
                        _refreshedUtc = DateTime.UtcNow;
                        _error = null;
                    }
                    else
                    {
                        // Parsed to nothing: their shape moved. Keep whatever
                        // is cached rather than replacing it with emptiness.
                        _error = "the index could not be read";
                    }
                }
                else
                {
                    _refreshedUtc = DateTime.UtcNow;
                    Touch(path);
                }
            }

            return _index;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads <see cref="_index"/> from whichever local copy is current — memory (a no-op if it is
    /// already set), else the cache file when <see cref="CacheIsAuthoritative"/>, else the shipped
    /// snapshot. Never touches the network: this is the read side of the load order S1/S2 describe,
    /// shared by <see cref="IndexAsync"/> and Refresh's own bookkeeping (which needs to know the
    /// current shard set before it can walk them, even when the index fetch itself only came back a
    /// 304). Caller holds <see cref="_gate"/>.
    /// </summary>
    private void EnsureIndexLoaded()
    {
        if (_index is not null)
        {
            return;
        }

        var path = CachePath("search-index.json");
        if (LoadFromCacheOrSnapshot(path, "search-index.json") is { } text)
        {
            _index = new NpcIndex(NpcReferenceFormat.ParseIndex(text), NpcReferenceFormat.ParseIndexZones(text));
        }
    }

    /// <summary>
    /// One file's current text, read locally only: the cache copy when <see cref="CacheIsAuthoritative"/>,
    /// else the shipped snapshot's copy, else null — "not known" (ADR-020 Decision 1's amendment).
    /// </summary>
    private string? LoadFromCacheOrSnapshot(string path, string file) =>
        (CacheIsAuthoritative(path) ? ReadCached(path) : null) ?? _snapshot.Read(file);

    /// <summary>
    /// Whether the on-disk cache copy of a file should be trusted over the shipped snapshot: it
    /// exists, and — when a snapshot is shipped — was written strictly after the snapshot was taken,
    /// which is to say a Refresh put it there (S2: a cache file no newer than the snapshot is
    /// ignored, not deleted — it simply loses to the bundle). With no snapshot, existing on disk is
    /// the whole test, which is this store's behaviour from before the snapshot existed.
    /// </summary>
    private bool CacheIsAuthoritative(string path) =>
        File.Exists(path) && (_snapshot.SnapshotUtc is not { } snap || File.GetLastWriteTimeUtc(path) > snap);

    /// <summary>The stat block for one listing, fetching its shard the first time it is wanted.</summary>
    public async Task<NpcDetail?> DetailAsync(int id, CancellationToken ct = default)
    {
        var shard = await ShardAsync(NpcReferenceFormat.ShardOf(id), ct).ConfigureAwait(false);
        return shard.Data?.GetValueOrDefault(id);
    }

    /// <summary>
    /// Every NPC the site lists in a zone, by the zone's map short name.
    ///
    /// <para>The shard at each of the zone's client ids is read, and only the
    /// rows that say they stand in this zone are kept — by the site's short
    /// name, or by the place's name for a zone with two drawings. That check
    /// is what makes the id join safe to use: a wrong id costs one fetch and
    /// an honest "not known", never another zone's roster under this zone's
    /// heading.</para>
    /// </summary>
    public async Task<ZoneRoster> RosterAsync(string shortName, CancellationToken ct = default)
    {
        var entry = ZoneTable.Default.EntryFor(shortName);
        if (!_enabled || entry is null || entry.Ids.Count == 0)
        {
            return new ZoneRoster(shortName, entry?.DisplayName, false, []);
        }

        var placeKey = ZoneTable.Normalize(entry.DisplayName);
        foreach (var id in entry.Ids)
        {
            var shard = await ShardAsync(id, ct).ConfigureAwait(false);
            if (shard.Data is not { } loaded)
            {
                continue;
            }

            var here = new List<(NpcDetail Detail, NpcSpawnZone Zone)>();
            foreach (var detail in loaded.Values)
            {
                foreach (var zone in detail.Zones)
                {
                    if (zone.ShortName.Equals(shortName, StringComparison.OrdinalIgnoreCase) ||
                        ZoneTable.Normalize(zone.LongName) == placeKey)
                    {
                        here.Add((detail, zone));
                        break;
                    }
                }
            }

            if (here.Count > 0)
            {
                var npcs = here
                    .OrderBy(x => x.Detail.Level ?? int.MaxValue)
                    .ThenBy(x => x.Detail.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new ZoneRosterNpc(
                        x.Detail.Id, x.Detail.Name, x.Detail.Level, x.Detail.MaxLevel,
                        x.Zone.SpawnPoints, x.Zone.Locations))
                    .ToArray();
                return new ZoneRoster(shortName, here[0].Zone.LongName, true, npcs);
            }
        }

        return new ZoneRoster(shortName, entry.DisplayName, false, []);
    }

    /// <summary>
    /// One shard's data, and whether fetching it actually reached the network — the second part is
    /// what the atlas walk paces itself on (<see cref="ShardAsync"/>), and a caller that does not
    /// care (<see cref="DetailAsync"/>, <see cref="RosterAsync"/>) just reads <see cref="Data"/>.
    /// </summary>
    private readonly record struct ShardLoad(IReadOnlyDictionary<int, NpcDetail>? Data, bool Network, string? Error = null);

    /// <summary>
    /// One shard: from memory, then — with a snapshot shipped — a cache file only when it is newer
    /// than the snapshot, then the snapshot's own copy, then an empty shard, memoised for the run,
    /// meaning "not known" rather than "not fetched yet"; never the network (ADR-020 Decision 1's
    /// amendment). With no snapshot, today's behaviour is unchanged below: a disk copy younger than
    /// <see cref="ShardMaxAge"/> with no request at all, then a conditional GET carrying its stored
    /// ETag — the same shape as <see cref="IndexAsync"/>'s own daily check, but weekly (F35, ADR-023
    /// Decision 7). A 304 keeps the cached copy and freshens its write time so it is not due again
    /// for another <see cref="ShardMaxAge"/>; a failure of any kind — a timeout, an empty body, a
    /// body that no longer parses — keeps the cached copy too. Never load-bearing, never replaced by
    /// emptiness, the same rule <see cref="IndexAsync"/> follows for its own copy.
    /// </summary>
    private async Task<ShardLoad> ShardAsync(int shard, CancellationToken ct)
    {
        if (!_enabled)
        {
            return new ShardLoad(null, false);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_shards.TryGetValue(shard, out var loaded))
            {
                return new ShardLoad(loaded, false);
            }

            var file = $"npcs-{shard}.json";
            var path = CachePath(file);

            if (_snapshot.SnapshotUtc is not null)
            {
                var text = LoadFromCacheOrSnapshot(path, file);
                var loadedShard = text is not null
                    ? NpcReferenceFormat.ParseShard(text)
                    : new Dictionary<int, NpcDetail>();
                _shards[shard] = loadedShard;
                return new ShardLoad(loadedShard, false);
            }

            IReadOnlyDictionary<int, NpcDetail>? cached = null;
            if (ReadCached(path) is { } cachedText)
            {
                var parsed = NpcReferenceFormat.ParseShard(cachedText);
                if (parsed.Count > 0)
                {
                    cached = parsed;
                }
            }

            if (cached is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) <= ShardMaxAge)
            {
                _shards[shard] = cached;
                return new ShardLoad(cached, false);
            }

            var fetch = await _source
                .GetAsync(NpcReferenceFormat.ShardPath(shard * 1000), EtagFor(file), ct)
                .ConfigureAwait(false);

            if (fetch.Missing)
            {
                // No such shard: a zone the site does not cover. Remembered for the run so a
                // roster is not asked for twice, and not an error — the site is fine, it just has
                // nothing here. It still counts as a shard the walk read (Decision 7): the site
                // answered.
                var empty = new Dictionary<int, NpcDetail>();
                _shards[shard] = empty;
                return new ShardLoad(empty, true);
            }

            if (fetch.Failed)
            {
                _error = fetch.Error;
                if (cached is not null)
                {
                    _shards[shard] = cached;
                }

                return new ShardLoad(cached, true, fetch.Error);
            }

            if (!fetch.Modified)
            {
                // 304: the copy on disk is current.
                Touch(path);
                if (cached is not null)
                {
                    _shards[shard] = cached;
                }

                return new ShardLoad(cached, true);
            }

            if (fetch.Content is { Length: > 0 })
            {
                var parsed = NpcReferenceFormat.ParseShard(fetch.Content);
                if (parsed.Count > 0)
                {
                    _shards[shard] = parsed;
                    Write(path, fetch.Content, file, fetch.ETag);
                    return new ShardLoad(parsed, true);
                }

                // Parsed to nothing: their shape moved. Keep whatever is cached rather than
                // replacing it with emptiness.
                if (cached is not null)
                {
                    _shards[shard] = cached;
                }

                return new ShardLoad(cached, true, "a shard could not be read");
            }

            return new ShardLoad(cached, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- Refresh (ADR-020 Decision 1's amendment): the only path to the network -----------------

    /// <summary>
    /// Starts a Refresh: the index, then every shard the (possibly new) index names, one at a time,
    /// each conditional on the ETag of whichever copy is currently in use. Idempotent while one is
    /// already running (a double click starts nothing extra); a no-op when disabled, because a
    /// server started with <c>--no-reference</c> must not put a request on the wire for any button.
    /// Unlike <see cref="StartAtlas"/>, this is meant to be pressed again: <see cref="_refreshStarted"/>
    /// resets once the walk finishes, so a later press starts a fresh one.
    /// </summary>
    public ReferenceStatus StartRefresh()
    {
        if (_enabled && Interlocked.CompareExchange(ref _refreshStarted, 1, 0) == 0)
        {
            _refreshFilesTotal = 0;
            _refreshFilesChecked = 0;
            _refreshFilesChanged = 0;
            _refreshFinishedUtc = null;
            _refreshError = null;
            _refreshTask = Task.Run(RefreshWalkAsync);
        }

        return Status();
    }

    /// <summary>Test seam, matching <see cref="AtlasTask"/>: await this instead of polling <see cref="Status"/> in a loop.</summary>
    internal Task? RefreshTask => _refreshTask;

    private async Task RefreshWalkAsync()
    {
        try
        {
            _refreshFilesTotal = 1; // the index; a shard count follows once it is read
            await RefreshIndexAsync(CancellationToken.None).ConfigureAwait(false);

            List<int> shards;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Whatever the index refresh just wrote, or — on a 304, a failure, or a first-ever
                // Refresh nobody has read anything through yet — whatever the local load order
                // (memory, then cache, then the snapshot) already has. Never a fetch of its own.
                EnsureIndexLoaded();
                shards = ShardsOf(_index);
            }
            finally
            {
                _gate.Release();
            }

            _refreshFilesTotal = 1 + shards.Count;

            for (var i = 0; i < shards.Count; i++)
            {
                await RefreshShardAsync(shards[i], CancellationToken.None).ConfigureAwait(false);

                // A pause "between them", not after the last one — same reasoning as the atlas
                // walk's own (WalkAsync): the final file's completion must not wait on a gap
                // nobody sees the other side of.
                if (i < shards.Count - 1)
                {
                    await Task.Delay(_atlasPause).ConfigureAwait(false);
                }
            }
        }
        catch (Exception e)
        {
            // Never throws out of a fire-and-forget background task (same rule WalkAsync follows,
            // Gotcha 1): nothing awaits this except the test seam above.
            _refreshError = e.Message;
        }
        finally
        {
            // A shard's *content* can change without its *count* changing, so AtlasAsync's own
            // memoisation (keyed on _shards.Count) would not notice — force it to rebuild.
            _atlasBuiltFrom = -1;
            _refreshFinishedUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _refreshStarted, 0);
        }
    }

    /// <summary>The ETag to carry for a file's conditional GET: the cache sidecar's when the cache copy is in use, else the snapshot's own (S4).</summary>
    private string? RefreshEtagFor(string path, string file) =>
        CacheIsAuthoritative(path) ? EtagFor(file) : _snapshot.EtagFor(file);

    /// <summary>The text of the copy a Refresh would replace — cache when it is in use, else the snapshot's — so a 200 can be checked against it.</summary>
    private string? TextInUse(string path, string file) =>
        CacheIsAuthoritative(path) ? ReadCached(path) : _snapshot.Read(file);

    // A 200 whose body is the copy already in use is not a change. The site's validator is not
    // to be trusted for this: measured on 2026-09-22, thirty-five minutes after the snapshot was
    // taken, a Refresh got a 200 for eleven of eighty files whose bytes had not moved at all —
    // and re-sending one of those its own current ETag still got a 200, while other files
    // answered 304 to the same request. Without this check every Refresh would rewrite those
    // files, stamp the cache as "refreshed just now", and report changes that did not happen.
    private static bool SameBytes(string? inUse, string fetched) =>
        inUse is not null && string.Equals(inUse, fetched, StringComparison.Ordinal);

    private static bool SameIndex(IReadOnlyList<NpcIndexEntry> a, IReadOnlyList<NpcIndexEntry> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => pair.First == pair.Second);

    private async Task RefreshIndexAsync(CancellationToken ct)
    {
        const string file = "search-index.json";
        var path = CachePath(file);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var fetch = await _source
                .GetAsync(NpcReferenceFormat.IndexPath, RefreshEtagFor(path, file), ct)
                .ConfigureAwait(false);
            _refreshFilesChecked++;

            if (fetch.Failed)
            {
                _refreshError = fetch.Error;
                return;
            }

            if (!fetch.Modified)
            {
                return; // 304: whichever copy is in use is still current. Nothing written.
            }

            if (fetch.Content is { Length: > 0 })
            {
                var entries = NpcReferenceFormat.ParseIndex(fetch.Content);
                if (entries.Count > 0)
                {
                    // The bundled index is trimmed to the rows the app reads and the site's is
                    // whole, so bytes cannot say whether it moved; the parsed rows can.
                    var inUse = TextInUse(path, file);
                    if (inUse is not null && SameIndex(NpcReferenceFormat.ParseIndex(inUse), entries))
                    {
                        return; // a 200 that changed nothing; see SameBytes
                    }

                    _index = new NpcIndex(entries, NpcReferenceFormat.ParseIndexZones(fetch.Content));
                    Write(path, fetch.Content, file, fetch.ETag);
                    _refreshFilesChanged++;
                }
                else
                {
                    // Their shape moved. Keep whatever is in use rather than replacing it with
                    // emptiness — the same rule the no-snapshot fetch path follows.
                    _refreshError = "the index could not be read";
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshShardAsync(int shard, CancellationToken ct)
    {
        var file = $"npcs-{shard}.json";
        var path = CachePath(file);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var fetch = await _source
                .GetAsync(NpcReferenceFormat.ShardPath(shard * 1000), RefreshEtagFor(path, file), ct)
                .ConfigureAwait(false);
            _refreshFilesChecked++;

            if (fetch.Missing)
            {
                return; // the site still has nothing here — not an error, same as ShardAsync's own reading of a 404.
            }

            if (fetch.Failed)
            {
                _refreshError = fetch.Error;
                return;
            }

            if (!fetch.Modified)
            {
                return; // 304
            }

            if (fetch.Content is { Length: > 0 })
            {
                if (SameBytes(TextInUse(path, file), fetch.Content))
                {
                    return; // a 200 that changed nothing; see SameBytes
                }

                var parsed = NpcReferenceFormat.ParseShard(fetch.Content);
                if (parsed.Count > 0)
                {
                    _shards[shard] = parsed;
                    Write(path, fetch.Content, file, fetch.ETag);
                    _refreshFilesChanged++;
                }
                else
                {
                    _refreshError = "a shard could not be read";
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// What "refreshed 3 days ago" means on screen (S4/S7): the newest write time among the cache
    /// files on disk that are newer than the snapshot, read straight off the filesystem rather than
    /// tracked in memory, so it survives a restart the same way the cache itself does. With no
    /// snapshot every cache file counts, since there is no floor to compare against.
    /// </summary>
    private DateTime? DiskRefreshedUtc()
    {
        if (!Directory.Exists(_root))
        {
            return null;
        }

        DateTime? newest = null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
            {
                if (Path.GetFileName(file).Equals("etags.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // bookkeeping, not a data file
                }

                var written = File.GetLastWriteTimeUtc(file);
                if (_snapshot.SnapshotUtc is { } snap && written <= snap)
                {
                    continue;
                }

                if (newest is null || written > newest)
                {
                    newest = written;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return newest;
    }

    private string CachePath(string file) => Path.Combine(_root, file);

    private static string? ReadCached(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Atomic write plus the ETag beside it, matching every other store here.</summary>
    private void Write(string path, string content, string file, string? etag)
    {
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
            SaveEtag(file, etag);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A 304 means the copy on disk is current; say so by its timestamp.</summary>
    private static void Touch(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string EtagPath => Path.Combine(_root, "etags.json");

    private string? EtagFor(string file)
    {
        try
        {
            if (!File.Exists(EtagPath))
            {
                return null;
            }

            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(EtagPath));
            return map is not null && map.TryGetValue(file, out var etag) ? etag : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void SaveEtag(string file, string? etag)
    {
        if (string.IsNullOrEmpty(etag))
        {
            return;
        }

        try
        {
            var map = File.Exists(EtagPath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(EtagPath)) ?? []
                : [];
            map[file] = etag;
            var temp = EtagPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(map));
            File.Move(temp, EtagPath, overwrite: true);
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
