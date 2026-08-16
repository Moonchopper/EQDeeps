using System.Text.Json;
using System.Text.Json.Serialization;
using EQDeeps.Core.Cache;
using EQDeeps.Core.Maps;

namespace EQDeeps.Server;

/// <summary>
/// The labels of every map file the world graph has been built from, on disk
/// (<c>cache\map-labels-&lt;build&gt;.json</c>), so the next build reads a few
/// thousand file stats instead of two hundred megabytes of geometry (issue
/// #59; ADR-018 §6).
///
/// <para>The graph needs one thing from each map — its <c>P</c> records, the
/// labelled points whose <c>to_Zone</c> text names an exit — and getting them
/// meant reading the whole file, because a map is one text stream and the
/// labels are scattered through 3.2 million segments. That is ~2.7 s per
/// launch on the owner's install, paid on the first click of the World view
/// and again after every restart. The labels themselves are ~36,000 records:
/// a few megabytes of JSON, read in tens of milliseconds.</para>
///
/// <para>Same shape as the log cache, one level down: cache the expensive
/// <i>input</i> (a file's labels), never the derived answer (the graph). The
/// graph is rebuilt from the labels every time, so a change to
/// <see cref="ZoneGraph"/> or the zone table never invalidates anything.
/// Each entry is validated against its file's size and last-write time, so a
/// player who edits one map re-parses one map, and the whole file is stamped
/// with the Core build that wrote it — the label grammar lives there. Entries
/// are keyed by full path, so pointing the library at a different folder
/// misses cleanly and switching back is still warm; entries whose files are
/// gone are dropped when the cache is next written.</para>
///
/// <para>Recomputable and, like every cache here, not allowed to fail
/// anything: an unreadable file starts empty, an unwritable one is simply not
/// written, and a miss parses the map exactly as before.</para>
/// </summary>
public sealed class MapLabelCache
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Entry>? _entries;
    private bool _dirty;

    public MapLabelCache(string? root = null, Guid? build = null)
    {
        // One file per Core build, for the same reason the log caches are:
        // a build can only read its own (the label grammar is Core's), and a
        // single shared file would have a dev build and the installed one
        // taking turns rewriting it. LogCacheStore.Sweep keeps this bounded.
        _path = Path.Combine(
            root ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQDeeps"),
            "cache", FileNameFor(build ?? LogCache.CoreVersion));
    }

    /// <summary>The file name a build's label cache carries; the sweep matches on the prefix.</summary>
    public static string FileNameFor(Guid build) =>
        "map-labels-" + Convert.ToHexString(build.ToByteArray().AsSpan(0, 8)) + ".json";

    /// <summary>Where the cache lives, for tests and for the curious.</summary>
    public string FilePath => _path;

    /// <summary>Files whose labels were parsed rather than served since the cache was loaded — the window onto the hit rate.</summary>
    public int Parsed { get; private set; }

    /// <summary>Files whose labels were served from the cache since it was loaded.</summary>
    public int Served { get; private set; }

    /// <summary>
    /// The labels-only layer for one map file: from the cache when the file's
    /// size and last-write time still match, otherwise parsed afresh and
    /// remembered. Null when the file cannot be read at all — vanished or
    /// locked, which in a folder the player edits is a layer lost, not an
    /// error. Bounds are the labels' own, exactly as a labels-only parse
    /// would have computed them.
    /// </summary>
    public MapLayer? LabelsFor(string path, int index)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var size = info.Length;
        var modified = info.LastWriteTimeUtc.Ticks;

        lock (_gate)
        {
            var entries = Load();
            if (entries.TryGetValue(path, out var hit) && hit.Size == size && hit.Modified == modified)
            {
                Served++;
                return hit.ToLayer(index);
            }
        }

        MapLayer layer;
        try
        {
            layer = MapFileParser.Parse(File.ReadAllText(path), index, labelsOnly: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        lock (_gate)
        {
            Load()[path] = Entry.From(layer, size, modified);
            _dirty = true;
            Parsed++;
        }

        return layer;
    }

    /// <summary>
    /// Writes the cache if anything was parsed since it was last written,
    /// dropping entries whose files no longer exist. Atomic (temp + move),
    /// like every store here. Never throws.
    /// </summary>
    public void Save()
    {
        lock (_gate)
        {
            if (!_dirty || _entries is null)
            {
                return;
            }

            foreach (var gone in _entries.Keys.Where(k => !File.Exists(k)).ToList())
            {
                _entries.Remove(gone);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                using (var stream = File.Create(temp))
                {
                    JsonSerializer.Serialize(stream, new Document(LogCache.CoreVersion, _entries), Json);
                }

                File.Move(temp, _path, overwrite: true);
                _dirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not written this time; the next build tries again.
            }
        }
    }

    /// <summary>
    /// The entries, read on first use. A file that is not there, does not
    /// parse, or was written by another Core build yields an empty set — the
    /// label grammar is Core's, and a build that reads labels differently
    /// must not trust the last one's.
    /// </summary>
    private Dictionary<string, Entry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        try
        {
            if (File.Exists(_path))
            {
                using var stream = File.OpenRead(_path);
                var doc = JsonSerializer.Deserialize<Document>(stream, Json);
                if (doc?.CoreVersion == LogCache.CoreVersion && doc.Files is not null)
                {
                    return _entries = new Dictionary<string, Entry>(doc.Files, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record Document(Guid CoreVersion, Dictionary<string, Entry> Files);

    private sealed record Entry(long Size, long Modified, int Malformed, List<Label> Labels)
    {
        public static Entry From(MapLayer layer, long size, long modified) => new(
            size,
            modified,
            layer.Malformed,
            layer.Labels.Select(l => new Label(
                l.At.X, l.At.Y, l.At.Z, l.Color.R, l.Color.G, l.Color.B, l.Size, l.Text)).ToList());

        public MapLayer ToLayer(int index)
        {
            var labels = new List<MapLabel>(Labels.Count);
            var bounds = MapBounds.Empty;
            foreach (var l in Labels)
            {
                var label = new MapLabel(new MapPoint(l.X, l.Y, l.Z), new MapColor(l.R, l.G, l.B), l.S, l.T);
                labels.Add(label);
                bounds = bounds.Add(label.At);
            }

            return new MapLayer(index, Array.Empty<MapLine>(), labels, bounds, Malformed);
        }
    }

    /// <summary>Short property names on purpose: there are ~36,000 of these.</summary>
    private sealed record Label(float X, float Y, float Z, byte R, byte G, byte B, int S, string T);
}
