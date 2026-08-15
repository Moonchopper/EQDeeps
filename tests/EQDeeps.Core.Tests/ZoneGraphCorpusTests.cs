using EQDeeps.Core.Maps;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// Builds the world graph from a real install and checks it is actually
/// navigable. The unit tests prove the algorithm; only this proves the data
/// supports it, and the data is community annotation rather than game truth.
///
/// <para>Opt-in via <c>EQDEEPS_MAPS</c>, like <see cref="MapCorpusTests"/>.</para>
/// </summary>
public class ZoneGraphCorpusTests
{
    private static ZoneGraph? Build(out int mapped)
    {
        mapped = 0;
        var root = Environment.GetEnvironmentVariable("EQDEEPS_MAPS");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return null;
        }

        var layers = new Dictionary<string, List<MapLayer>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var index = 0;
            if (stem.Length > 2 && stem[^2] == '_' && char.IsAsciiDigit(stem[^1]))
            {
                index = stem[^1] - '0';
                stem = stem[..^2];
            }

            if (!layers.TryGetValue(stem, out var list))
            {
                layers[stem] = list = new List<MapLayer>();
            }

            list.Add(MapFileParser.Parse(File.ReadAllText(file), index));
        }

        mapped = layers.Count;
        var maps = layers.Select(kv => ZoneMap.FromLayers(kv.Key.ToLowerInvariant(), kv.Value)).ToList();
        return ZoneGraph.Build(maps, ZoneTable.Default);
    }

    [Fact]
    public void TheWorldIsConnectedEnoughToRouteAcross()
    {
        var graph = Build(out var mapped);
        if (graph is null)
        {
            return;
        }

        Assert.True(mapped > 500, $"Only {mapped} zones found.");
        Assert.True(graph.ConnectionCount > 500, $"Only {graph.ConnectionCount} connections resolved.");

        // Antonica to Faydwer: the crossing every new player learns, and the
        // one that proves boat and continent links both survived resolution.
        var route = graph.Route("qeynos", "gfaydark");
        Assert.True(route is { Count: > 1 }, "No route from South Qeynos to The Greater Faydark.");
        Assert.Equal("qeynos", route![0]);
        Assert.Equal("gfaydark", route[^1]);
    }

    [Fact]
    public void MostZonesWithLabelsLandInOneComponent()
    {
        var graph = Build(out _);
        if (graph is null)
        {
            return;
        }

        // Largest connected component, by flood fill.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var largest = 0;

        foreach (var start in graph.Zones)
        {
            if (!seen.Add(start))
            {
                continue;
            }

            var size = 1;
            var queue = new Queue<string>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                foreach (var next in graph.Neighbours(queue.Dequeue()))
                {
                    if (seen.Add(next))
                    {
                        size++;
                        queue.Enqueue(next);
                    }
                }
            }

            largest = Math.Max(largest, size);
        }

        // Not a round number chosen for comfort: zones whose maps carry no
        // to_ labels at all are isolated by construction, so the ceiling is
        // well under the zone count. This asserts the connected part is a
        // world rather than a handful of pairs.
        Assert.True(largest > 100, $"Largest connected component is only {largest} zones.");
    }
}
