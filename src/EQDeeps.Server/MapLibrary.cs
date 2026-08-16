using System.Collections.Concurrent;
using System.Text.Json;
using EQDeeps.Core.Maps;

namespace EQDeeps.Server;

/// <summary>One zone the library can draw, and where its geometry came from.</summary>
/// <param name="Era">
/// The earliest expansion the place exists in — <c>classic</c>, <c>kunark</c>…
/// — or absent when the table cannot say. Absent means "shown under every era
/// filter", never "hidden"; see <see cref="ZoneEras"/>.
/// </param>
/// <param name="EraSource">
/// <c>id</c> when the era came from the zone's client-id band, <c>curated</c>
/// when it was set by hand. Carried for the same reason as
/// <paramref name="NameSource"/>: a hand-set value deserves a different
/// confidence, and it inherits whatever doubt the name pairing already had.
/// </param>
/// <param name="Sets">
/// The map sets holding this zone, best first. A player who has copied Brewall's
/// set in has two drawings of the same place, and which one they mean is theirs
/// to say — see <see cref="MapLibrary"/>.
/// </param>
public sealed record MapCatalogEntry(
    string ShortName,
    string? DisplayName,
    string? NameSource,
    string? Era,
    string? EraSource,
    IReadOnlyList<string> Sets);

/// <summary>
/// What the app found on disk. <paramref name="Roots"/> is reported even when
/// empty so the UI can say *where it looked* rather than only that it failed.
/// </summary>
/// <param name="UserRoot">
/// The folder the user nominated, if any — so the UI can show what it is
/// currently set to rather than only offering to set it.
/// </param>
public sealed record MapCatalog(
    bool Found,
    IReadOnlyList<string> Roots,
    IReadOnlyList<MapCatalogEntry> Zones,
    string? UserRoot = null);

/// <summary>
/// The player's own map files, read from their EverQuest install (F27,
/// ADR-016). Nothing is bundled and nothing is written — this is a read-only
/// view of a folder the game already maintains.
///
/// <para>Discovery reuses <see cref="LogDiscovery.InstallRoots"/>, which already
/// handles the awkward part: EQ Legends installs beside EverQuest under a
/// publisher folder that is routinely on a different drive from %PUBLIC%.
/// A <c>maps</c> folder under any of those roots is a map set.</para>
///
/// <para>Parsed zones are cached, because the largest is 26,383 segments and a
/// player pans around the same zone all evening. The cache is bounded and
/// simply cleared when full: a map is cheap to re-read, and an eviction policy
/// tuned for a working set of "one or two zones" would be ceremony.</para>
/// </summary>
public sealed class MapLibrary
{
    /// <summary>
    /// The client's own folder first. Brewall's maps are more detailed and many
    /// players prefer them, but preferring them by default would silently show
    /// a different drawing from the one the game shows.
    /// </summary>
    private static readonly string[] SetOrder = { "default", "brewalls" };

    private const int MaxCachedZones = 24;

    private readonly string? _rootOverride;
    private readonly DocumentStore? _settings;
    private readonly MapLabelCache _labels;
    private readonly ConcurrentDictionary<string, ZoneMap> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private MapCatalog? _catalog;
    private ZoneGraph? _graph;

    /// <param name="rootOverride">
    /// A maps folder to use instead of discovery. Carries <c>--mapRoot</c> so
    /// tests never depend on a game being installed, the same pattern the other
    /// stores follow.
    /// </param>
    /// <param name="settings">
    /// Where the folder the *user* pointed at is kept, for the machine that has
    /// the logs but not the game — a copied maps folder, or an install on a
    /// drive discovery does not walk. Read through the document store rather
    /// than a private file because it is a correction the user made, and those
    /// live with their dashboards.
    /// </param>
    /// <param name="labels">
    /// The on-disk cache of every map's labels, so the world graph does not
    /// re-read two hundred megabytes of geometry on every launch. Required
    /// rather than defaulted, because the only sensible default would write
    /// into the real %AppData%, and a caller that forgot to redirect it
    /// should not get that silently.
    /// </param>
    public MapLibrary(string? rootOverride, DocumentStore? settings, MapLabelCache labels)
    {
        _rootOverride = rootOverride;
        _settings = settings;
        _labels = labels;
    }

    /// <summary>
    /// The folder the user nominated, or null. <c>--mapRoot</c> deliberately
    /// does not appear here: a test's redirect is not a user preference, and
    /// showing it as one would let a test's path be saved back into a real
    /// document.
    /// </summary>
    public string? UserRoot
    {
        get
        {
            if (_settings?.Read("map-settings") is not { } doc)
            {
                return null;
            }

            return doc.ValueKind == JsonValueKind.Object
                && doc.TryGetProperty("root", out var root)
                && root.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(root.GetString())
                ? root.GetString()
                : null;
        }
    }

    /// <summary>
    /// Points the library at a folder, or clears it with null. Returns false
    /// with a reason rather than throwing, because every failure here is
    /// something the user should be told in the box they typed into.
    /// </summary>
    public bool TrySetUserRoot(string? path, out string? error)
    {
        error = null;

        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!Directory.Exists(path))
            {
                error = "There is no folder at that path.";
                return false;
            }

            // A folder that holds no maps is almost always the install root
            // rather than the maps folder inside it — a mistake worth naming
            // exactly, since the fix is one directory away.
            if (!LooksLikeMaps(path))
            {
                error = Directory.Exists(Path.Combine(path, "maps"))
                    ? "That is the install folder. Point at the 'maps' folder inside it."
                    : "No EverQuest map files in that folder.";
                return false;
            }
        }

        Persist(string.IsNullOrWhiteSpace(path) ? null : path);

        // Everything derived is now stale: which zones exist, their geometry,
        // and the world built from their labels.
        lock (_gate)
        {
            _catalog = null;
            _graph = null;
            _cache.Clear();
        }

        return true;
    }

    /// <summary>
    /// One parseable map file is enough. Reads at most a handful, because this
    /// runs while the user waits and a maps folder holds ~1900 files.
    /// </summary>
    private static bool LooksLikeMaps(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*.txt").Take(8))
            {
                var layer = MapFileParser.Parse(File.ReadAllText(file));
                if (layer.Lines.Count > 0 || layer.Labels.Count > 0)
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    /// <summary>
    /// Read-modify-write: the same document carries the user's per-zone map
    /// choices, which belong to the client and must survive this.
    /// </summary>
    private void Persist(string? path)
    {
        if (_settings is null)
        {
            return;
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (_settings.Read("map-settings") is { ValueKind: JsonValueKind.Object } existing)
        {
            foreach (var property in existing.EnumerateObject())
            {
                fields[property.Name] = property.Value.Clone();
            }
        }

        fields.Remove("root");

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in fields)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            if (path is not null)
            {
                writer.WriteString("root", path);
            }

            writer.WriteEndObject();
        }

        _settings.Write("map-settings", JsonDocument.Parse(buffer.ToArray()).RootElement);
    }

    public ZoneTable Table => ZoneTable.Default;

    /// <summary>Every zone with a map, with the name the log would call it.</summary>
    public MapCatalog Catalog()
    {
        if (_catalog is { } cached)
        {
            return cached;
        }

        lock (_gate)
        {
            return _catalog ??= BuildCatalog();
        }
    }

    /// <summary>
    /// The map short names that could be the zone the log just named, best
    /// first, filtered to those actually present on this machine.
    /// </summary>
    public IReadOnlyList<string> MapsFor(string zoneName)
    {
        var available = Catalog();
        var known = available.Zones.Select(z => z.ShortName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ZoneTable.Default.MapsFor(zoneName).Where(known.Contains).ToArray();
    }

    /// <summary>
    /// Parses a zone's layers, or null when there is no such map. A named set
    /// wins if it has the zone; otherwise the first set that does.
    /// </summary>
    public ZoneMap? Load(string shortName, string? set = null)
    {
        var entry = Catalog().Zones.FirstOrDefault(
            z => string.Equals(z.ShortName, shortName, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return null;
        }

        var chosen = set is not null && entry.Sets.Contains(set, StringComparer.OrdinalIgnoreCase)
            ? set
            : entry.Sets[0];

        var key = $"{chosen}/{entry.ShortName}";
        if (_cache.TryGetValue(key, out var hit))
        {
            return hit;
        }

        var map = Read(entry.ShortName, chosen);
        if (map is null)
        {
            return null;
        }

        if (_cache.Count >= MaxCachedZones)
        {
            _cache.Clear();
        }

        _cache[key] = map;
        return map;
    }

    /// <summary>
    /// The world graph. Built once and held: it needs every map's labels, which
    /// means consulting ~1900 files, and the answer does not change while the
    /// app is open.
    ///
    /// <para>Only the labels are kept — the geometry is discarded as it goes,
    /// so this costs a pass over the files rather than 3.2 million segments of
    /// resident memory. And the labels come from <see cref="MapLabelCache"/>
    /// when the file has not changed since they were last read, so on every
    /// launch but the first the pass is a stat per file, not a read.</para>
    /// </summary>
    public ZoneGraph Graph()
    {
        if (_graph is { } cached)
        {
            return cached;
        }

        lock (_gate)
        {
            if (_graph is not null)
            {
                return _graph;
            }

            var maps = new List<ZoneMap>();

            foreach (var entry in Catalog().Zones)
            {
                var layers = new List<MapLayer>();

                // Every set, not just the preferred drawing. Which map a zone is
                // *drawn* from is a matter of taste; which exits exist is not,
                // and the two sets annotate different ones — the client's maps
                // label 94 zones' exits, Brewall's label 528. Taking only the
                // preferred set produced a 15-hop South Qeynos to Greater
                // Faydark route that went via Erudin and Neriak, because the
                // short way was written down only in the set being ignored.
                foreach (var set in entry.Sets)
                {
                    foreach (var (path, index) in FilesFor(entry.ShortName, set))
                    {
                        // Labels only: the graph never draws anything, and the
                        // geometry it would otherwise parse and discard is 99%
                        // of the bytes.
                        var layer = _labels.LabelsFor(path, index);
                        if (layer is not null)
                        {
                            layers.Add(layer);
                        }
                    }
                }

                if (layers.Count > 0)
                {
                    maps.Add(ZoneMap.FromLayers(entry.ShortName, layers));
                }
            }

            // Whatever had to be parsed this time is on disk for next time.
            _labels.Save();
            return _graph = ZoneGraph.Build(maps, ZoneTable.Default);
        }
    }

    private MapCatalog BuildCatalog()
    {
        var roots = Roots();
        var bySet = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, set) in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(root, "*.txt", SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var (stem, _) = SplitLayer(Path.GetFileNameWithoutExtension(file));

                if (!bySet.TryGetValue(stem, out var sets))
                {
                    bySet[stem] = sets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }

                if (!sets.TryGetValue(set, out var list))
                {
                    sets[set] = list = new List<string>();
                }

                list.Add(file);
            }
        }

        var zones = bySet
            .Select(kv =>
            {
                var entry = ZoneTable.Default.EntryFor(kv.Key);
                return new MapCatalogEntry(
                    kv.Key,
                    entry?.DisplayName,
                    entry?.Source.ToString().ToLowerInvariant(),
                    entry?.Era,
                    entry?.EraSource?.ToString().ToLowerInvariant(),
                    SetOrder.Where(kv.Value.ContainsKey).ToArray());
            })
            // Named zones first, then alphabetically: a player looking for a
            // zone knows its display name, and the unnamed tail is short names
            // the table has not resolved. Maps sharing a name are ordered by
            // short name so the tie is the same every run — the first one
            // stands for the place in the world graph and in the zone list.
            .OrderBy(z => z.DisplayName is null)
            .ThenBy(z => z.DisplayName ?? z.ShortName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(z => z.ShortName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MapCatalog(
            zones.Length > 0,
            roots.Select(r => r.Root).ToArray(),
            zones,
            UserRoot);
    }

    /// <summary>
    /// Candidate map folders, best set first. An override replaces discovery
    /// entirely rather than adding to it, so a test cannot accidentally read a
    /// real install.
    /// </summary>
    private List<(string Root, string Set)> Roots()
    {
        var roots = new List<(string, string)>();

        // --mapRoot beats the user's own setting, so a test can never be
        // steered by whatever a real document happens to contain.
        var pinned = _rootOverride is { Length: > 0 } ? _rootOverride : UserRoot;

        if (!string.IsNullOrWhiteSpace(pinned))
        {
            roots.Add((pinned, "default"));
            roots.Add((Path.Combine(pinned, "brewalls"), "brewalls"));
            return roots;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (dir, _) in LogDiscovery.InstallRoots())
        {
            var maps = Path.Combine(dir, "maps");
            if (!Directory.Exists(maps) || !seen.Add(Path.GetFullPath(maps)))
            {
                continue;
            }

            roots.Add((maps, "default"));

            var brewalls = Path.Combine(maps, "brewalls");
            if (Directory.Exists(brewalls))
            {
                roots.Add((brewalls, "brewalls"));
            }
        }

        return roots;
    }

    private IEnumerable<(string Path, int Index)> FilesFor(string shortName, string set)
    {
        foreach (var (root, candidate) in Roots())
        {
            if (!string.Equals(candidate, set, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root))
            {
                continue;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(root, shortName + "*.txt", SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var (stem, index) = SplitLayer(Path.GetFileNameWithoutExtension(file));

                // The wildcard also matches longer names — "guk*" catches
                // gukbottom as well as guktop.
                if (string.Equals(stem, shortName, StringComparison.OrdinalIgnoreCase))
                {
                    yield return (file, index);
                }
            }
        }
    }

    private ZoneMap? Read(string shortName, string set)
    {
        var layers = new List<MapLayer>();

        foreach (var (path, index) in FilesFor(shortName, set).OrderBy(f => f.Index))
        {
            var layer = SafeParse(path, index);
            if (layer is not null)
            {
                layers.Add(layer);
            }
        }

        return layers.Count == 0 ? null : ZoneMap.FromLayers(shortName, layers);
    }

    /// <summary>
    /// A map that vanished or locked mid-read costs its layer, not the zone.
    /// These files live in a folder the player edits while the app is running.
    /// </summary>
    private static MapLayer? SafeParse(string path, int index)
    {
        try
        {
            return MapFileParser.Parse(File.ReadAllText(path), index);
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

    /// <summary>
    /// Splits <c>gukbottom_1</c> into the zone and its layer number. Only 1–3
    /// are layers; a name that merely ends in a digit (<c>arena2</c>,
    /// <c>qeynos2</c>) is its own zone.
    /// </summary>
    private static (string Stem, int Index) SplitLayer(string fileName)
    {
        if (fileName.Length > 2 && fileName[^2] == '_' && fileName[^1] is >= '1' and <= '3')
        {
            return (fileName[..^2].ToLowerInvariant(), fileName[^1] - '0');
        }

        return (fileName.ToLowerInvariant(), 0);
    }
}
