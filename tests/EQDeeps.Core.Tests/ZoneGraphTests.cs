using EQDeeps.Core.Maps;
using Xunit;

namespace EQDeeps.Core.Tests;

public class ZoneGraphTests
{
    private static readonly ZoneTable Table = ZoneTable.Parse(
        """
        gfaydark	The Greater Faydark	name
        butcher	Butcherblock Mountains	curated
        crushbone	Clan Crushbone	curated
        oot	The Ocean of Tears	curated
        felwithea	Northern Felwithe	curated
        """);

    private static ZoneMap Map(string shortName, params string[] labels) =>
        ZoneMap.FromLayers(shortName, new[]
        {
            new MapLayer(
                0,
                Array.Empty<MapLine>(),
                labels.Select(t => new MapLabel(new MapPoint(1, 2, 3), new MapColor(0, 0, 0), 3, t)).ToArray(),
                MapBounds.Empty,
                0),
        });

    [Theory]
    [InlineData("to Butcherblock Mountains", "Butcherblock Mountains")]
    [InlineData("from The Ocean of Tears", "The Ocean of Tears")]
    [InlineData("to Clan Crushbone (click the door)", "Clan Crushbone")]
    [InlineData("To Northern Felwithe", "Northern Felwithe")]
    public void ReadsADestinationOutOfALabel(string label, string expected)
    {
        Assert.Equal(new[] { expected }, ZoneGraph.Destinations(label));
    }

    /// <summary>One point can be the way to several places at once.</summary>
    [Fact]
    public void SplitsLabelsThatNameSeveralDestinations()
    {
        Assert.Equal(
            new[] { "Butcherblock", "Ocean of Tears", "Qeynos" },
            ZoneGraph.Destinations("to Butcherblock/Ocean of Tears/Qeynos"));

        Assert.Equal(
            new[] { "Erudin", "South Qeynos" },
            ZoneGraph.Destinations("to Erudin or South Qeynos"));
    }

    [Fact]
    public void IgnoresLabelsThatAreNotConnections()
    {
        Assert.Empty(ZoneGraph.Destinations("Gruppip (Wizard Spells)"));
        Assert.Empty(ZoneGraph.Destinations("Note: complete the event to open the floor"));

        // "to Ak" is a truncated label in the real corpus; two characters
        // cannot name a zone and guessing which one is worse than dropping it.
        Assert.Empty(ZoneGraph.Destinations("to Ak"));
    }

    [Fact]
    public void BuildsEdgesFromLabelsAndResolvesThemToMaps()
    {
        var graph = ZoneGraph.Build(
            new[]
            {
                Map("gfaydark", "to Butcherblock Mountains", "to Clan Crushbone"),
                Map("butcher"),
                Map("crushbone"),
            },
            Table);

        var exits = graph.From("gfaydark");
        Assert.Equal(2, exits.Count);
        Assert.Contains(exits, e => e.ToShortName == "butcher" && e.ToDisplayName == "Butcherblock Mountains");
        Assert.Equal(new MapPoint(1, 2, 3), exits[0].At);
    }

    /// <summary>
    /// A mapmaker labels the side they were standing on, so most connections
    /// are written down once. Routing has to see them from both ends anyway.
    /// </summary>
    [Fact]
    public void ConnectionsAreTraversableFromEitherEnd()
    {
        var graph = ZoneGraph.Build(
            new[] { Map("gfaydark", "to Butcherblock Mountains"), Map("butcher") },
            Table);

        Assert.Contains("butcher", graph.Neighbours("gfaydark"));
        Assert.Contains("gfaydark", graph.Neighbours("butcher"));
    }

    /// <summary>
    /// A label naming a zone this machine has no map for is not a connection
    /// the player can be shown, so it is not an edge. This bites in the real
    /// corpus because the table maps one display name onto every map claiming
    /// it — "The Ocean of Tears" is both oot and oceanoftears — and only the
    /// installed one may be linked.
    /// </summary>
    [Fact]
    public void LabelsPointingAtZonesWithNoMapHereAreDropped()
    {
        var graph = ZoneGraph.Build(
            new[] { Map("gfaydark", "to Butcherblock Mountains") },
            Table);

        Assert.Empty(graph.From("gfaydark"));
        Assert.DoesNotContain("butcher", graph.Zones);
    }

    [Fact]
    public void LabelsPointingAtUnknownZonesAreDropped()
    {
        var graph = ZoneGraph.Build(new[] { Map("gfaydark", "to Some Place With No Map") }, Table);

        Assert.Empty(graph.From("gfaydark"));
    }

    [Fact]
    public void ASelfReferenceIsNotAConnection()
    {
        var graph = ZoneGraph.Build(new[] { Map("gfaydark", "to The Greater Faydark") }, Table);

        Assert.Empty(graph.From("gfaydark"));
    }

    [Fact]
    public void RoutesByFewestZones()
    {
        var graph = ZoneGraph.Build(
            new[]
            {
                Map("crushbone", "to The Greater Faydark"),
                Map("gfaydark", "to Butcherblock Mountains"),
                Map("butcher", "to The Ocean of Tears"),
                Map("oot"),
            },
            Table);

        Assert.Equal(
            new[] { "crushbone", "gfaydark", "butcher", "oot" },
            graph.Route("crushbone", "oot"));
    }

    [Fact]
    public void RouteToSelfIsASingleStep()
    {
        var graph = ZoneGraph.Build(new[] { Map("gfaydark", "to Butcherblock Mountains") }, Table);

        Assert.Equal(new[] { "gfaydark" }, graph.Route("gfaydark", "gfaydark"));
    }

    [Fact]
    public void ReturnsNoRouteWhenTheWorldIsDisconnected()
    {
        var graph = ZoneGraph.Build(
            new[] { Map("gfaydark", "to Clan Crushbone"), Map("oot") },
            Table);

        Assert.Null(graph.Route("gfaydark", "oot"));
        Assert.Null(graph.Route("gfaydark", "nosuchzone"));
    }

    /// <summary>
    /// The era filter reaches routing as an allow-list. A shortcut through a
    /// zone that is not allowed is not taken — the longer way round is the
    /// answer, and no way round at all is "no route", never the shortcut.
    /// </summary>
    [Fact]
    public void RoutesOnlyThroughAllowedZones()
    {
        // crushbone - gfaydark - butcher - oot, with felwithea as a shortcut
        // straight from crushbone to oot.
        var graph = ZoneGraph.Build(
            new[]
            {
                Map("crushbone", "to The Greater Faydark", "to Northern Felwithe"),
                Map("gfaydark", "to Butcherblock Mountains"),
                Map("butcher", "to The Ocean of Tears"),
                Map("felwithea", "to The Ocean of Tears"),
                Map("oot"),
            },
            Table);

        Assert.Equal(new[] { "crushbone", "felwithea", "oot" }, graph.Route("crushbone", "oot"));

        Assert.Equal(
            new[] { "crushbone", "gfaydark", "butcher", "oot" },
            graph.Route("crushbone", "oot", z => z != "felwithea"));

        // An end that is not allowed is not somewhere the route can start or
        // finish, either.
        Assert.Null(graph.Route("crushbone", "felwithea", z => z != "felwithea"));
        Assert.Null(graph.Route("crushbone", "oot", z => z != "oot"));
    }
}
