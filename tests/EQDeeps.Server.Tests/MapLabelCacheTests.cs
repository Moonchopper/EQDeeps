using System.Text.Json;
using EQDeeps.Core.Cache;
using EQDeeps.Core.Maps;
using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// The world graph's label cache (issue #59, ADR-018 §6): a map's labels are
/// read from disk once and served from the cache until the file changes, the
/// cache survives a restart, and nothing about it can make the graph wrong —
/// a stale, foreign, or corrupt cache falls back to parsing.
/// </summary>
public sealed class MapLabelCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));

    public MapLabelCacheTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "maps"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Map(string name, string text)
    {
        var path = Path.Combine(_dir, "maps", name);
        File.WriteAllText(path, text);
        return path;
    }

    private const string Faydark =
        """
        L 0, 0, 0, 100, 100, 0, 64, 64, 64
        P 10, 20, 0, 0, 0, 240, 3, to_Butcherblock_Mountains
        P 50, 50, 0, 0, 0, 0, 2, Tunare`s_Grove,_a_note
        """;

    [Fact]
    public void ServesFromTheCacheUntilTheFileChanges()
    {
        var path = Map("gfaydark.txt", Faydark);
        var cache = new MapLabelCache(_dir);

        var first = cache.LabelsFor(path, 0)!;
        Assert.Equal(1, cache.Parsed);
        Assert.Equal(2, first.Labels.Count);
        Assert.Empty(first.Lines);
        Assert.Equal("to Butcherblock Mountains", first.Labels[0].Text);
        Assert.Equal(new MapPoint(10, 20, 0), first.Labels[0].At);
        Assert.False(first.Bounds.IsEmpty);

        // Same size and time: served, not parsed — proven by rewriting the
        // file's content while pinning both, which a real edit never does.
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, Faydark.Replace("Butcherblock", "Xutcherblock"));
        File.SetLastWriteTimeUtc(path, stamp);
        var second = cache.LabelsFor(path, 0)!;
        Assert.Equal(1, cache.Parsed);
        Assert.Equal("to Butcherblock Mountains", second.Labels[0].Text);
        Assert.Equal(first.Bounds, second.Bounds);
        Assert.Equal(first.Malformed, second.Malformed);

        // A real edit moves the time and is re-read.
        File.SetLastWriteTimeUtc(path, stamp.AddSeconds(5));
        var third = cache.LabelsFor(path, 0)!;
        Assert.Equal(2, cache.Parsed);
        Assert.Equal("to Xutcherblock Mountains", third.Labels[0].Text);
    }

    [Fact]
    public void SurvivesARestartAndPrunesTheGone()
    {
        var a = Map("gfaydark.txt", Faydark);
        var b = Map("butcher.txt", "P 5, 5, 0, 0, 0, 240, 3, to_The_Greater_Faydark");
        var gone = Map("oldzone.txt", "P 1, 1, 0, 0, 0, 240, 3, to_Nowhere");

        var writer = new MapLabelCache(_dir);
        Assert.NotNull(writer.LabelsFor(a, 0));
        Assert.NotNull(writer.LabelsFor(b, 0));
        Assert.NotNull(writer.LabelsFor(gone, 0));
        File.Delete(gone);
        writer.Save();
        Assert.True(File.Exists(writer.FilePath));

        var reader = new MapLabelCache(_dir);
        var layer = reader.LabelsFor(a, 0)!;
        Assert.NotNull(reader.LabelsFor(b, 0));
        Assert.Equal(0, reader.Parsed);
        Assert.Equal(2, layer.Labels.Count);
        Assert.Equal("Tunare`s Grove, a note", layer.Labels[1].Text);

        // The vanished file was dropped at save time.
        var doc = JsonDocument.Parse(File.ReadAllText(reader.FilePath));
        var files = doc.RootElement.GetProperty("files");
        Assert.Equal(2, files.EnumerateObject().Count());
        Assert.Equal(LogCache.CoreVersion, files.ValueKind == JsonValueKind.Object
            ? doc.RootElement.GetProperty("coreVersion").GetGuid()
            : Guid.Empty);
    }

    [Fact]
    public void AForeignBuildsCacheAndACorruptOneAreIgnored()
    {
        var a = Map("gfaydark.txt", Faydark);
        var writer = new MapLabelCache(_dir);
        Assert.NotNull(writer.LabelsFor(a, 0));
        writer.Save();

        // Same shape, another parser build.
        var text = File.ReadAllText(writer.FilePath)
            .Replace(LogCache.CoreVersion.ToString(), Guid.NewGuid().ToString());
        File.WriteAllText(writer.FilePath, text);
        var foreign = new MapLabelCache(_dir);
        Assert.NotNull(foreign.LabelsFor(a, 0));
        Assert.Equal(1, foreign.Parsed);

        File.WriteAllText(writer.FilePath, "{ not json");
        var corrupt = new MapLabelCache(_dir);
        Assert.NotNull(corrupt.LabelsFor(a, 0));
        Assert.Equal(1, corrupt.Parsed);
        corrupt.Save();

        // And it healed itself.
        var healed = new MapLabelCache(_dir);
        Assert.NotNull(healed.LabelsFor(a, 0));
        Assert.Equal(0, healed.Parsed);
    }

    /// <summary>
    /// The cache used to recompute a served layer's <c>Bounds</c> from its
    /// labels, which quietly shrank it to whatever the labels happened to
    /// cover. A file whose geometry reaches further than any label — the
    /// normal case, since a zone's walls are usually bigger than its exits —
    /// is what would have caught that: a served layer must carry the same
    /// box a fresh parse would, because <see cref="EQDeeps.Core.Maps.ZoneGraph.Bearing(string, string)"/>
    /// measures against it.
    /// </summary>
    [Fact]
    public void AServedLayersBoundsEqualTheParsedLayersWhenGeometryReachesFurtherThanAnyLabel()
    {
        const string text =
            """
            L -500, -500, 0, 500, 500, 0, 64, 64, 64
            P 10, 20, 0, 0, 0, 240, 3, to_Somewhere
            """;
        var path = Map("wide.txt", text);

        var parsed = MapFileParser.Parse(text, labelsOnly: true);

        var writer = new MapLabelCache(_dir);
        var firstServe = writer.LabelsFor(path, 0)!;
        Assert.Equal(parsed.Bounds, firstServe.Bounds);

        // From a fresh cache instance too, so this is the persisted value
        // round-tripping through the file rather than a value still held in
        // memory from the parse that just happened.
        writer.Save();
        var reader = new MapLabelCache(_dir);
        var served = reader.LabelsFor(path, 0)!;
        Assert.Equal(0, reader.Parsed);
        Assert.Equal(parsed.Bounds, served.Bounds);
    }

    [Fact]
    public void AMissingFileIsNullNotAnError()
    {
        var cache = new MapLabelCache(_dir);
        Assert.Null(cache.LabelsFor(Path.Combine(_dir, "maps", "nope.txt"), 0));
        cache.Save();
        Assert.False(File.Exists(cache.FilePath)); // nothing to write
    }
}
