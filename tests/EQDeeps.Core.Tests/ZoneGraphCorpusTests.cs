using EQDeeps.Core.Maps;
using Xunit;
using Xunit.Abstractions;

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
    private readonly ITestOutputHelper _output;

    public ZoneGraphCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

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

    /// <summary>
    /// The map doc's own oracle (eq-map-format.md §3), restated as bearings
    /// rather than recorded from this app's output: real outdoor borders
    /// where the two zones' own maps were checked against each other by
    /// hand, not a golden file. If this fails on a real install, the bearing
    /// definition is wrong — the fix is in <see cref="ZoneGraph"/>, not here.
    ///
    /// <para><b>2026-09-20, flagged for the PM:</b> the brief's own oracle
    /// named "Qeynos Hills → South Qeynos: west" (the doc's table). On this
    /// install, neither <c>qeytoqrg.txt</c> nor <c>qeynos.txt</c> — either
    /// map set, every layer — carries a <c>to</c>/<c>from</c> label naming
    /// the other zone at all; <see cref="ZoneGraph.Bearing(string, string)"/>
    /// correctly returns null for a pair the maps never connect, so the gap
    /// is in the acceptance criterion's example, not in the formula. Swapped
    /// in "both halves of the Qeynos pair" instead — the doc's own next two
    /// rows, and the one pairing in that table the doc calls out as mutually
    /// confirming — which the corpus does carry, both directions. See the
    /// implementer's escalation for the full finding.</para>
    /// </summary>
    [Fact]
    public void BearingsAgreeWithTheHandStatedCompassDirections()
    {
        var graph = Build(out _);
        if (graph is null)
        {
            return;
        }

        AssertWest(graph, "ecommons", "commons");            // East Commonlands -> West Commonlands
        AssertEast(graph, "commons", "ecommons");             // West Commonlands -> East Commonlands
        AssertEast(graph, "freeportwest", "freeporteast");    // West Freeport -> East Freeport

        // "Both halves of the Qeynos pair" (map doc §3) — substituted for the
        // brief's named "Qeynos Hills -> South Qeynos", which no map on this
        // install actually labels; see the class doc above.
        AssertNorth(graph, "qeynos", "qeynos2");   // South Qeynos -> North Qeynos
        AssertSouth(graph, "qeynos2", "qeynos");   // North Qeynos -> South Qeynos

        // The owner's original complaint (2026-09-20): Blackburrow's own map
        // puts Everfrost Peaks on its east side and Qeynos Hills to its south.
        AssertEast(graph, "blackburrow", "everfrost");
        AssertSouth(graph, "blackburrow", "qeytoqrg");
    }

    private static void AssertEast(ZoneGraph graph, string from, string to) =>
        AssertDirection(graph, from, to, "east", b => b.X > 0 && MathF.Abs(b.X) > MathF.Abs(b.Y));

    private static void AssertWest(ZoneGraph graph, string from, string to) =>
        AssertDirection(graph, from, to, "west", b => b.X < 0 && MathF.Abs(b.X) > MathF.Abs(b.Y));

    private static void AssertSouth(ZoneGraph graph, string from, string to) =>
        AssertDirection(graph, from, to, "south", b => b.Y > 0 && MathF.Abs(b.Y) > MathF.Abs(b.X));

    private static void AssertNorth(ZoneGraph graph, string from, string to) =>
        AssertDirection(graph, from, to, "north", b => b.Y < 0 && MathF.Abs(b.Y) > MathF.Abs(b.X));

    private static void AssertDirection(
        ZoneGraph graph, string from, string to, string expected, Func<MapBearing, bool> matches)
    {
        var bearing = graph.Bearing(from, to);
        Assert.True(
            bearing is { } b && matches(b),
            $"{from}->{to}: expected {expected}, got {(bearing is { } v ? $"({v.X:F3}, {v.Y:F3})" : "null")}.");
    }

    /// <summary>
    /// The metric behind the base-layer design decision (<see cref="ZoneGraph.BaseFrame"/>),
    /// recomputed here against the live corpus rather than pasted as a
    /// comment, so it cannot go stale. "Both sides" means each of the two
    /// connections making up a pair has its own non-null per-connection
    /// bearing — not <see cref="ZoneGraph.Bearing(string, string)"/>, which is
    /// defined even with only one side labelled and would make "both" mean
    /// nothing. The printed counts are the deliverable: they are what
    /// replaces the architect's prototype numbers in the domain doc.
    /// </summary>
    [Fact]
    public void ReportsHowOftenBothEndsOfADoublyLabelledConnectionAgree()
    {
        var graph = Build(out _);
        if (graph is null)
        {
            return;
        }

        var pairs = 0;
        var within90 = 0;
        var within45 = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var zone in graph.Zones)
        {
            foreach (var outgoing in graph.From(zone))
            {
                if (outgoing.Bearing is not { } forward)
                {
                    continue;
                }

                var key = string.CompareOrdinal(zone, outgoing.ToShortName) <= 0
                    ? zone + "|" + outgoing.ToShortName
                    : outgoing.ToShortName + "|" + zone;
                if (!seen.Add(key))
                {
                    continue;
                }

                var backward = graph.From(outgoing.ToShortName)
                    .FirstOrDefault(c => string.Equals(c.ToShortName, zone, StringComparison.OrdinalIgnoreCase)
                        && c.Bearing is not null)
                    ?.Bearing;

                if (backward is not { } back)
                {
                    continue;
                }

                pairs++;

                // A doorway drawn from either side points at the *other*
                // zone's middle, so agreeing drawings produce roughly
                // opposite vectors — compare the outgoing bearing against
                // the incoming one negated.
                var angle = AngleBetween(forward, new MapBearing(-back.X, -back.Y));
                if (angle <= 90)
                {
                    within90++;
                }

                if (angle <= 45)
                {
                    within45++;
                }
            }
        }

        _output.WriteLine(
            $"Both-sides agreement: {pairs} doubly-labelled pairs, {within90} agree within 90 degrees, "
            + $"{within45} within 45 degrees.");

        Assert.True(pairs > 0, "No doubly-labelled pairs found — is EQDEEPS_MAPS pointed at a real install?");
    }

    private static float AngleBetween(MapBearing a, MapBearing b)
    {
        var magA = MathF.Sqrt((a.X * a.X) + (a.Y * a.Y));
        var magB = MathF.Sqrt((b.X * b.X) + (b.Y * b.Y));
        if (magA == 0f || magB == 0f)
        {
            return 180f; // no direction on one side; treat as maximally disagreeing
        }

        var cos = Math.Clamp(((a.X * b.X) + (a.Y * b.Y)) / (magA * magB), -1f, 1f);
        return MathF.Acos(cos) * (180f / MathF.PI);
    }
}
