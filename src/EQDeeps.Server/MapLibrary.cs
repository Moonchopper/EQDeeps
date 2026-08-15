using System.Collections.Concurrent;
using EQDeeps.Core.Maps;

namespace EQDeeps.Server;

/// <summary>One zone the library can draw, and where its geometry came from.</summary>
/// <param name="Sets">
/// The map sets holding this zone, best first. A player who has copied Brewall's
/// set in has two drawings of the same place, and which one they mean is theirs
/// to say — see <see cref="MapLibrary"/>.
/// </param>
public sealed record MapCatalogEntry(
    string ShortName,
    string? DisplayName,
    string? NameSource,
    IReadOnlyList<string> Sets);

/// <summary>
/// What the app found on disk. <paramref name="Roots"/> is reported even when
/// empty so the UI can say *where it looked* rather than only that it failed.
/// </summary>
public sealed record MapCatalog(
    bool Found,
    IReadOnlyList<string> Roots,
    IReadOnlyList<MapCatalogEntry> Zones);

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
    private readonly ConcurrentDictionary<string, ZoneMap> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private MapCatalog? _catalog;
    private ZoneGraph? _graph;

    /// <param name="rootOverride">
    /// A maps folder to use instead of discovery. Carries <c>--mapRoot</c> so
    /// tests never depend on a game being installed, the same pattern the other
    /// stores follow.
    /// </param>
    public MapLibrary(string? rootOverride = null) => _rootOverride = rootOverride;

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
    /// means reading ~1900 files, and the answer does not change while the app
    /// is open.
    ///
    /// <para>Only the labels are kept — the geometry is discarded as it goes,
    /// so this costs a pass over the files rather than 3.2 million segments of
    /// resident memory.</para>
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
                        var layer = SafeParse(path, index, labelsOnly: true);
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
                    SetOrder.Where(kv.Value.ContainsKey).ToArray());
            })
            // Named zones first, then alphabetically: a player looking for a
            // zone knows its display name, and the unnamed tail is short names
            // the table has not resolved.
            .OrderBy(z => z.DisplayName is null)
            .ThenBy(z => z.DisplayName ?? z.ShortName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MapCatalog(
            zones.Length > 0,
            roots.Select(r => r.Root).ToArray(),
            zones);
    }

    /// <summary>
    /// Candidate map folders, best set first. An override replaces discovery
    /// entirely rather than adding to it, so a test cannot accidentally read a
    /// real install.
    /// </summary>
    private List<(string Root, string Set)> Roots()
    {
        var roots = new List<(string, string)>();

        if (!string.IsNullOrWhiteSpace(_rootOverride))
        {
            roots.Add((_rootOverride, "default"));
            roots.Add((Path.Combine(_rootOverride, "brewalls"), "brewalls"));
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
    private static MapLayer? SafeParse(string path, int index, bool labelsOnly = false)
    {
        try
        {
            return MapFileParser.Parse(File.ReadAllText(path), index, labelsOnly);
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
