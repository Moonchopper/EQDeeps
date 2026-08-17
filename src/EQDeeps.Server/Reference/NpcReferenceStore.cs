using System.Text.Json;
using EQDeeps.Core.Maps;
using EQDeeps.Core.Reference;

namespace EQDeeps.Server.Reference;

/// <summary>What the reference layer can currently answer, and why not when it cannot.</summary>
public sealed record ReferenceStatus(
    bool Available,
    string Source,
    string HomeUrl,
    int Names,
    int Listings,
    DateTime? RefreshedUtc,
    string? Error);

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
/// </summary>
public sealed class NpcReferenceStore
{
    /// <summary>How stale the index may get before a conditional GET is worth it.</summary>
    private static readonly TimeSpan IndexMaxAge = TimeSpan.FromDays(1);

    private readonly IReferenceSource _source;
    private readonly string _root;
    private readonly bool _enabled;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, IReadOnlyDictionary<int, NpcDetail>> _shards = [];

    private NpcIndex? _index;
    private DateTime? _refreshedUtc;
    private bool _revalidated;
    private string? _error;

    public NpcReferenceStore(IReferenceSource source, string? root = null, bool enabled = true)
    {
        _source = source;
        _enabled = enabled;
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
        _refreshedUtc,
        _enabled ? _error : "reference lookups are switched off (--no-reference)");

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
            if (_index is null && ReadCached(path) is { } cached)
            {
                _index = new NpcIndex(NpcReferenceFormat.ParseIndex(cached), NpcReferenceFormat.ParseIndexZones(cached));
                _refreshedUtc = File.GetLastWriteTimeUtc(path);
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

    /// <summary>The stat block for one listing, fetching its shard the first time it is wanted.</summary>
    public async Task<NpcDetail?> DetailAsync(int id, CancellationToken ct = default)
    {
        var shard = await ShardAsync(NpcReferenceFormat.ShardOf(id), ct).ConfigureAwait(false);
        return shard?.GetValueOrDefault(id);
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
            if (shard is null)
            {
                continue;
            }

            var here = new List<(NpcDetail Detail, NpcSpawnZone Zone)>();
            foreach (var detail in shard.Values)
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

    /// <summary>One shard, from memory, then disk, then the network; null when it cannot be had.</summary>
    private async Task<IReadOnlyDictionary<int, NpcDetail>?> ShardAsync(int shard, CancellationToken ct)
    {
        if (!_enabled)
        {
            return null;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_shards.TryGetValue(shard, out var loaded))
            {
                return loaded;
            }

            var file = $"npcs-{shard}.json";
            var path = CachePath(file);
            if (ReadCached(path) is { } cached)
            {
                var parsed = NpcReferenceFormat.ParseShard(cached);
                if (parsed.Count > 0)
                {
                    _shards[shard] = parsed;
                    return parsed;
                }
            }

            var fetch = await _source
                .GetAsync(NpcReferenceFormat.ShardPath(shard * 1000), EtagFor(file), ct)
                .ConfigureAwait(false);
            if (fetch.Missing)
            {
                // No such shard: a zone the site does not cover. Remembered
                // for the run so a roster is not asked for twice, and not an
                // error — the site is fine, it just has nothing here.
                _shards[shard] = new Dictionary<int, NpcDetail>();
                return _shards[shard];
            }

            if (fetch.Failed)
            {
                _error = fetch.Error;
                return null;
            }

            if (fetch.Modified && fetch.Content is { Length: > 0 })
            {
                var parsed = NpcReferenceFormat.ParseShard(fetch.Content);
                _shards[shard] = parsed;
                if (parsed.Count > 0)
                {
                    Write(path, fetch.Content, file, fetch.ETag);
                }

                return parsed;
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
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
