using EQDeeps.Core.Maps;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// Parses every map file of a real EverQuest install and asserts the whole
/// corpus comes back clean.
///
/// <para>Opt-in, because CI has no game install: point <c>EQDEEPS_MAPS</c> at a
/// <c>maps</c> folder to run it. That makes it the map-side twin of the fixture
/// corpus — the literal-string tests pin the format's shape, this one pins
/// coverage, and only the second kind catches a hand-edited file doing
/// something the format never documented.</para>
///
/// <para>Measured on a stock EQ Legends install of 1904 files: 3,244,827 segments
/// and 35,719 labels, zero malformed. That zero is the point of the test — the
/// two quirks the parser handles specially (commas inside labels, records that
/// run together) were both found this way rather than by reading the spec,
/// because the format has no spec.</para>
/// </summary>
public class MapCorpusTests
{
    [Fact]
    public void EveryMapInARealInstallParsesCleanly()
    {
        var root = Environment.GetEnvironmentVariable("EQDEEPS_MAPS");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        var files = Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);

        var drawn = 0L;
        var bad = new List<string>();

        foreach (var file in files)
        {
            var layer = MapFileParser.Parse(File.ReadAllText(file));
            drawn += layer.Lines.Count + layer.Labels.Count;

            if (layer.Malformed > 0)
            {
                bad.Add($"{Path.GetFileName(file)} ({layer.Malformed})");
            }
        }

        Assert.True(bad.Count == 0, "Malformed records in: " + string.Join(", ", bad.Take(20)));

        // A corpus that parses to nothing would also report zero malformed.
        Assert.True(drawn > 100_000, $"Only {drawn} records parsed from {files.Count} files.");
    }
}
