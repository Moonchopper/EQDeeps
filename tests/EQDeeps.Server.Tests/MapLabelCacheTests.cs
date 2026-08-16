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

    [Fact]
    public void AMissingFileIsNullNotAnError()
    {
        var cache = new MapLabelCache(_dir);
        Assert.Null(cache.LabelsFor(Path.Combine(_dir, "maps", "nope.txt"), 0));
        cache.Save();
        Assert.False(File.Exists(cache.FilePath)); // nothing to write
    }
}
