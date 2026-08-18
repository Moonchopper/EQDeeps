using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// The map library on its own, without a server around it — for the
/// behaviours that are about how it goes about its work rather than what it
/// answers. Everything here runs against a pinned folder, so it holds whether
/// or not this machine has EverQuest on it.
/// </summary>
public sealed class MapLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));

    private readonly string _maps;

    public MapLibraryTests()
    {
        _maps = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(Path.Combine(_maps, "brewalls"));
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

    private void Map(string set, string name, string text) =>
        File.WriteAllText(
            Path.Combine(set == "default" ? _maps : Path.Combine(_maps, set), name),
            text);

    /// <summary>
    /// Building the graph resolves the map folders once for the catalogue and
    /// once for the graph — not once per zone per set. Resolution is a full
    /// install discovery (processes, registry, drives) when nothing is pinned;
    /// done inside the per-zone lookup it ran seven hundred times on a real
    /// install, ~2.5 s, and hid behind the label cache's win because it
    /// happened whether the labels were cached or not. Nine zone-set pairs
    /// here, so a per-zone regression reads as eleven, not two.
    /// </summary>
    [Fact]
    public void GraphResolvesTheMapFoldersOncePerBuildNotOncePerZone()
    {
        Map("default", "gfaydark.txt", "P 1, 1, 0, 0, 0, 240, 3, to_Butcherblock_Mountains");
        Map("default", "gfaydark_1.txt", "P 2, 2, 0, 0, 0, 0, 3, A_Layer");
        Map("default", "butcher.txt", "P 1, 1, 0, 0, 0, 240, 3, to_The_Greater_Faydark");
        Map("default", "oot.txt", "P 1, 1, 0, 0, 0, 240, 3, to_Butcherblock_Mountains");
        Map("default", "cauldron.txt", "P 1, 1, 0, 0, 0, 240, 3, to_The_Estate_of_Unrest");
        Map("default", "unrest.txt", "L 1, 1, 1, 2, 2, 2, 0, 0, 255");
        Map("default", "crushbone.txt", "P 1, 1, 0, 0, 0, 240, 3, to_The_Greater_Faydark");
        Map("brewalls", "gfaydark.txt", "P 1, 1, 0, 0, 0, 240, 3, to_Clan_Crushbone");
        Map("brewalls", "butcher.txt", "P 1, 1, 0, 0, 0, 240, 3, to_Dagnor`s_Cauldron");
        Map("brewalls", "kaladima.txt", "P 1, 1, 0, 0, 0, 240, 3, to_Butcherblock_Mountains");

        var library = new MapLibrary(_maps, settings: null, new MapLabelCache(_dir));

        var graph = library.Graph();

        // Sanity: the fixture really did produce a world with both sets in it,
        // so the count below is a count over real work.
        Assert.True(graph.Zones.Count >= 6, $"only {graph.Zones.Count} zones");
        Assert.Equal(9, library.Catalog().Zones.Sum(z => z.Sets.Count));

        Assert.Equal(2, library.RootResolutions);

        // Held once built: asking again resolves nothing.
        library.Graph();
        Assert.Equal(2, library.RootResolutions);
    }
}
