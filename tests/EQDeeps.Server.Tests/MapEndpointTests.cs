using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// The map endpoints end to end over real Kestrel (F27), against a synthetic
/// maps folder rather than a game install — <c>--mapRoot</c> exists so these
/// run on CI, where EverQuest is not.
///
/// <para>The fixture uses real zone names from the shipped table, because the
/// interesting behaviour is the join between a name off a zone line and a file
/// on disk. Using invented names would test the plumbing and skip the point.</para>
/// </summary>
public sealed class MapEndpointTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var maps = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(Path.Combine(maps, "brewalls"));

        // Faydwer in a row: Greater Faydark - Butcherblock - Ocean of Tears,
        // and Butcherblock - Dagnor's Cauldron - Unrest. Only the first link
        // is written from both ends; the rest are written once, which is the
        // normal case and the reason routing is undirected.
        //
        // The Plane of Knowledge is a shortcut from Greater Faydark straight to
        // Unrest — not real geography, but the era filter needs a zone from a
        // later expansion to refuse, and PoK is the one every player would
        // recognise as "not there yet" on a classic server (issue #57).
        File.WriteAllText(Path.Combine(maps, "gfaydark.txt"),
            """
            L 0, 0, 0, 100, 100, 0, 64, 64, 64
            L 100, 100, 0, 200, 0, 0, 64, 64, 64
            L 0, 0, 0, 0, 100, 0, 240, 0, 0
            P 10, 20, 0, 0, 0, 240, 3, to_Butcherblock_Mountains
            P 50, 50, 0, 0, 0, 0, 2, Tunare`s_Grove,_a_note
            """);
        File.WriteAllText(Path.Combine(maps, "gfaydark_1.txt"),
            "P 30, 40, 12, 0, 0, 0, 3, Second_Layer_Marker");
        File.WriteAllText(Path.Combine(maps, "butcher.txt"),
            """
            L 0, 0, 0, 10, 10, 0, 0, 204, 0
            P 5, 5, 0, 0, 0, 240, 3, to_The_Greater_Faydark
            P 9, 9, 0, 0, 0, 240, 3, to_The_Ocean_of_Tears_(Boat)
            P 1, 9, 0, 0, 0, 240, 3, to_Dagnor`s_Cauldron
            """);
        File.WriteAllText(Path.Combine(maps, "oot.txt"), "L 1, 1, 1, 2, 2, 2, 0, 0, 255");
        // A second drawing of the same place, as a revamp leaves behind: the
        // graph must show one Ocean of Tears, not two.
        File.WriteAllText(Path.Combine(maps, "oceanoftears.txt"), "L 1, 1, 1, 3, 3, 3, 0, 0, 255");
        File.WriteAllText(Path.Combine(maps, "cauldron.txt"),
            "P 1, 1, 0, 0, 0, 240, 3, to_The_Estate_of_Unrest");
        File.WriteAllText(Path.Combine(maps, "unrest.txt"), "L 1, 1, 1, 2, 2, 2, 0, 0, 255");
        File.WriteAllText(Path.Combine(maps, "poknowledge.txt"),
            """
            P 1, 1, 0, 0, 0, 240, 3, to_The_Greater_Faydark
            P 2, 2, 0, 0, 0, 240, 3, to_The_Estate_of_Unrest
            """);

        // Same zone in the other set, so "which drawing" is a real choice.
        File.WriteAllText(Path.Combine(maps, "brewalls", "gfaydark.txt"),
            "L 0, 0, 0, 5, 5, 0, 255, 0, 255");

        _app = ServerApp.Build(Args(maps));
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string[] Args(string maps) =>
    [
        "--urls", "http://127.0.0.1:0",
        "--recentLogsRoot", _dir,
        "--sampleLogRoot", _dir,
        "--updateRoot", _dir,
        "--mobRoot", _dir,
        "--attackRoot", _dir,
        "--itemRoot", _dir,
        "--storeRoot", _dir,
        "--cacheRoot", _dir,
        "--mapRoot", maps,
    ];

    private async Task<JsonElement> Get(string url)
    {
        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task CatalogNamesTheZonesItFound()
    {
        var catalog = await Get("/api/maps");

        Assert.True(catalog.GetProperty("found").GetBoolean());

        var zones = catalog.GetProperty("zones").EnumerateArray().ToList();
        Assert.Equal(7, zones.Count);

        var fay = zones.Single(z => z.GetProperty("shortName").GetString() == "gfaydark");
        Assert.Equal("The Greater Faydark", fay.GetProperty("displayName").GetString());

        // The set list is what lets the UI offer a choice of drawing.
        Assert.Equal(
            new[] { "default", "brewalls" },
            fay.GetProperty("sets").EnumerateArray().Select(s => s.GetString()).ToArray());
    }

    /// <summary>
    /// The provenance of a name reaches the client, because a hand-written
    /// pairing and a derived one do not deserve the same confidence (ADR-016).
    /// </summary>
    [Fact]
    public async Task CatalogSaysHowEachNameWasArrivedAt()
    {
        var zones = (await Get("/api/maps")).GetProperty("zones").EnumerateArray().ToList();
        var sources = zones
            .Select(z => z.GetProperty("nameSource").GetString())
            .ToList();

        Assert.All(sources, s => Assert.Contains(s, new[] { "name", "graph", "curated" }));
    }

    /// <summary>
    /// The era reaches the client with its own provenance, for the same reason
    /// the name does: it is a claim about the row, and a hand-set one deserves
    /// a different confidence from a derived one (issue #57).
    /// </summary>
    [Fact]
    public async Task CatalogSaysWhichEraEachZoneIsFrom()
    {
        var zones = (await Get("/api/maps")).GetProperty("zones").EnumerateArray().ToList();

        var fay = zones.Single(z => z.GetProperty("shortName").GetString() == "gfaydark");
        Assert.Equal("classic", fay.GetProperty("era").GetString());
        Assert.Equal("id", fay.GetProperty("eraSource").GetString());

        var pok = zones.Single(z => z.GetProperty("shortName").GetString() == "poknowledge");
        Assert.Equal("pop", pok.GetProperty("era").GetString());
    }

    [Fact]
    public async Task ResolvesAZoneLineNameToItsMap()
    {
        var resolved = await Get("/api/maps/resolve?zone=The%20Greater%20Faydark");

        Assert.Equal(
            new[] { "gfaydark" },
            resolved.GetProperty("shortNames").EnumerateArray().Select(s => s.GetString()).ToArray());
    }

    /// <summary>An instance's geometry is its open-world zone's.</summary>
    [Fact]
    public async Task ResolvesAnInstanceToTheSameMap()
    {
        var resolved = await Get("/api/maps/resolve?zone=The%20Greater%20Faydark%204%20(Refined)");

        Assert.Equal(
            new[] { "gfaydark" },
            resolved.GetProperty("shortNames").EnumerateArray().Select(s => s.GetString()).ToArray());
    }

    [Fact]
    public async Task ResolvingAnUnknownZoneIsEmptyRatherThanAnError()
    {
        var resolved = await Get("/api/maps/resolve?zone=Nowhere%20At%20All");

        Assert.Empty(resolved.GetProperty("shortNames").EnumerateArray());
    }

    [Fact]
    public async Task ServesGeometryGroupedByColour()
    {
        var map = await Get("/api/maps/gfaydark");

        Assert.Equal("The Greater Faydark", map.GetProperty("displayName").GetString());
        Assert.Equal("default", map.GetProperty("set").GetString());

        var layers = map.GetProperty("layers").EnumerateArray().ToList();
        Assert.Equal(2, layers.Count);

        var strokes = layers[0].GetProperty("strokes").EnumerateArray().ToList();
        Assert.Equal(2, strokes.Count);

        // Two grey segments share one group; the red one is its own.
        var grey = strokes.Single(s => s.GetProperty("r").GetInt32() == 64);
        Assert.Equal(12, grey.GetProperty("segments").GetArrayLength());

        var red = strokes.Single(s => s.GetProperty("r").GetInt32() == 240);
        Assert.Equal(6, red.GetProperty("segments").GetArrayLength());
    }

    [Fact]
    public async Task KeepsLayersApartAndRestoresLabelText()
    {
        var layers = (await Get("/api/maps/gfaydark")).GetProperty("layers").EnumerateArray().ToList();

        Assert.Equal(0, layers[0].GetProperty("index").GetInt32());
        Assert.Equal(1, layers[1].GetProperty("index").GetInt32());

        var second = layers[1].GetProperty("labels").EnumerateArray().Single();
        Assert.Equal("Second Layer Marker", second.GetProperty("text").GetString());

        // A comma inside a label survives the round trip.
        var note = layers[0].GetProperty("labels").EnumerateArray()
            .Select(l => l.GetProperty("text").GetString())
            .ToList();
        Assert.Contains("Tunare`s Grove, a note", note);
    }

    [Fact]
    public async Task ServesTheOtherSetOnRequest()
    {
        var map = await Get("/api/maps/gfaydark?set=brewalls");

        Assert.Equal("brewalls", map.GetProperty("set").GetString());
        Assert.Equal(1, map.GetProperty("layers").EnumerateArray().Single().GetProperty("segments").GetInt32());
    }

    [Fact]
    public async Task UnknownZoneIsANotFound()
    {
        var response = await _http.GetAsync("/api/maps/nosuchzone");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Building the graph leaves every map's labels in the cache under
    /// <c>--cacheRoot</c>, so the next launch does not read the maps again
    /// (issue #59). Asserted here, against the real wiring, because a
    /// MapLibrary built without the cache would still pass every other test.
    /// </summary>
    [Fact]
    public async Task GraphWritesTheLabelCacheUnderTheRedirect()
    {
        var first = await Get("/api/maps/graph");
        Assert.Equal(9, first.GetProperty("mapsRead").GetInt32());
        Assert.Equal(0, first.GetProperty("mapsRemembered").GetInt32());

        var path = Path.Combine(_dir, "cache", MapLabelCache.FileNameFor(EQDeeps.Core.Cache.LogCache.CoreVersion));
        Assert.True(File.Exists(path), $"Nothing was written to {path} — is the label cache wired?");
        var files = JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement.GetProperty("files");
        // Every map file the fixture wrote, in both sets, layer files included.
        Assert.Equal(9, files.EnumerateObject().Count());
        Assert.Contains(files.EnumerateObject(), f => f.Name.EndsWith("gfaydark_1.txt", StringComparison.OrdinalIgnoreCase));

        // A second launch against the same folders reads nothing and builds
        // the same graph.
        var again = ServerApp.Build(Args(Path.Combine(_dir, "maps")));
        await again.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(again.Urls.First()) };
            var second = await http.GetFromJsonAsync<JsonElement>("/api/maps/graph");
            Assert.Equal(0, second.GetProperty("mapsRead").GetInt32());
            Assert.Equal(9, second.GetProperty("mapsRemembered").GetInt32());
            Assert.Equal(first.GetProperty("edges").GetRawText(), second.GetProperty("edges").GetRawText());
            Assert.Equal(first.GetProperty("zones").GetRawText(), second.GetProperty("zones").GetRawText());
        }
        finally
        {
            await again.StopAsync();
            await again.DisposeAsync();
        }
    }

    [Fact]
    public async Task GraphJoinsTheZonesAndWritesEachEdgeOnce()
    {
        var graph = await Get("/api/maps/graph");

        var edges = graph.GetProperty("edges").EnumerateArray()
            .Select(e => e.GetProperty("from").GetString() + "->" + e.GetProperty("to").GetString())
            .OrderBy(s => s)
            .ToArray();

        // gfaydark<->butcher is labelled from both sides but is still one edge,
        // and butcher's one label to the Ocean of Tears is one edge to the one
        // Ocean of Tears node, however many drawings of it are on disk.
        Assert.Equal(
            new[]
            {
                "butcher->cauldron", "butcher->gfaydark", "butcher->oceanoftears",
                "cauldron->unrest", "gfaydark->poknowledge", "poknowledge->unrest",
            },
            edges);
    }

    /// <summary>
    /// A place with two drawings is one node that lists both, so the client
    /// can open whichever the user prefers and never shows a zone twice.
    /// </summary>
    [Fact]
    public async Task TwoDrawingsOfAPlaceAreOneNode()
    {
        var zones = (await Get("/api/maps/graph")).GetProperty("zones").EnumerateArray().ToList();

        var oceans = zones.Where(z => z.GetProperty("displayName").GetString() == "The Ocean of Tears").ToList();
        var ocean = Assert.Single(oceans);

        Assert.Equal("oceanoftears", ocean.GetProperty("shortName").GetString());
        Assert.Equal(
            new[] { "oceanoftears", "oot" },
            ocean.GetProperty("maps").EnumerateArray().Select(m => m.GetString()).ToArray());
    }

    /// <summary>
    /// The graph carries each zone's era and the ordered list of eras, so the
    /// client can hide by expansion without a copy of the list of its own.
    /// </summary>
    [Fact]
    public async Task GraphCarriesErasAndTheirOrder()
    {
        var graph = await Get("/api/maps/graph");

        var eras = graph.GetProperty("eras").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString())
            .ToList();
        Assert.Equal("classic", eras[0]);
        Assert.True(eras.IndexOf("kunark") < eras.IndexOf("pop"));

        var pok = graph.GetProperty("zones").EnumerateArray()
            .Single(z => z.GetProperty("shortName").GetString() == "poknowledge");
        Assert.Equal("pop", pok.GetProperty("era").GetString());
    }

    [Fact]
    public async Task RoutesAcrossTheWorldAndSaysHowEachHopIsUsed()
    {
        // Asked for by its other drawing's name; answered in places.
        var route = (await Get("/api/maps/route?from=gfaydark&to=oot"))
            .GetProperty("route").EnumerateArray().ToList();

        Assert.Equal(
            new[] { "gfaydark", "butcher", "oceanoftears" },
            route.Select(s => s.GetProperty("shortName").GetString()).ToArray());

        Assert.Equal("The Ocean of Tears", route[2].GetProperty("displayName").GetString());

        // The parenthetical is dropped from the destination but kept on the
        // step, because "(Boat)" is how you make the crossing.
        Assert.Contains("Boat", route[2].GetProperty("via").GetString()!);
    }

    /// <summary>
    /// With no era given, routing is exactly what it was: the shortest way,
    /// through whatever the labels join. With one, a zone from a later
    /// expansion is not there to be walked through — the longer classic way is
    /// the answer, and a later zone is not a destination at all.
    /// </summary>
    [Fact]
    public async Task AnEraKeepsRoutesInsideWhatTheServerHasUnlocked()
    {
        static string[] Steps(JsonElement route) =>
            route.GetProperty("route").EnumerateArray()
                .Select(s => s.GetProperty("shortName").GetString()!)
                .ToArray();

        var open = await Get("/api/maps/route?from=gfaydark&to=unrest");
        Assert.Equal(new[] { "gfaydark", "poknowledge", "unrest" }, Steps(open));

        var classic = await Get("/api/maps/route?from=gfaydark&to=unrest&era=classic");
        Assert.Equal(new[] { "gfaydark", "butcher", "cauldron", "unrest" }, Steps(classic));

        // Everything through Planes of Power is allowed again, so the shortcut is back.
        var pop = await Get("/api/maps/route?from=gfaydark&to=unrest&era=pop");
        Assert.Equal(new[] { "gfaydark", "poknowledge", "unrest" }, Steps(pop));

        var toPok = await Get("/api/maps/route?from=gfaydark&to=poknowledge&era=classic");
        Assert.False(toPok.GetProperty("found").GetBoolean());
    }

    /// <summary>
    /// An era this build does not know is refused rather than ignored: ignored
    /// would route through everything while the caller believed it had a filter.
    /// </summary>
    [Fact]
    public async Task AnUnknownEraIsRefused()
    {
        var response = await _http.GetAsync("/api/maps/route?from=gfaydark&to=unrest&era=atlantis");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// "No route" is stated, not implied. The serializer drops nulls, so a null
    /// route would reach the client as a missing property and be
    /// indistinguishable from a broken response.
    /// </summary>
    [Fact]
    public async Task ReportsNoRouteRatherThanInventingOne()
    {
        var response = await Get("/api/maps/route?from=oot&to=nosuchzone");

        Assert.False(response.GetProperty("found").GetBoolean());
        Assert.Empty(response.GetProperty("route").EnumerateArray());
    }

    /// <summary>
    /// The table maps one display name onto every map that claims it, so
    /// "West Freeport" is both freportw and freeportwest. Neither is on this
    /// machine, so a label to it is not an edge and it is not a node.
    /// </summary>
    [Fact]
    public async Task DoesNotLinkToZonesThisMachineCannotDraw()
    {
        var graph = await Get("/api/maps/graph");

        var zones = graph.GetProperty("zones").EnumerateArray()
            .Select(z => z.GetProperty("shortName").GetString())
            .ToArray();

        Assert.DoesNotContain("freportw", zones);
        Assert.DoesNotContain("freeportwest", zones);
    }
}
