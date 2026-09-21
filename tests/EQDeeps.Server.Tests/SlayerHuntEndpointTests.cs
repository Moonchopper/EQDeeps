using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EQDeeps.Core.Achievements;
using EQDeeps.Server.Reference;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// <c>GET /api/sessions/{id}/slayer/hunt</c> end to end (F35, ADR-023 Decisions 4-7, brief C of
/// wave F35-2): the ranked-zones answer over HTTP, built from both player exports and whatever the
/// atlas walk has loaded.
///
/// <para>Every test here injects a fake <see cref="IReferenceSource"/> through
/// <c>ServerApp.Build</c>'s test seam and starts its own <see cref="WebApplication"/> — nothing in
/// this file, or in the app it drives, ever reaches eqlbase.com.</para>
/// </summary>
public sealed class SlayerHuntEndpointTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));

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

    // Same reasoning as AchievementExportTests.Line / SlayerEndpointTests.Line: no literal tab
    // ever typed into source, so an editor cannot silently break the fixture.
    private static string Line(params string[] columns) => string.Join('\t', columns);

    private string InstallRoot => Path.Combine(_dir, "EverQuest Legends");

    private const string AchievementKey = "Slayer: Conquest/Multi Hunt";

    /// <summary>A toy world in EQLBase's own shapes: one shard (99), three NPCs, two races, one city.</summary>
    private sealed class FakeSource : IReferenceSource
    {
        public readonly List<string> Requested = [];

        public readonly Dictionary<string, string> Bodies = new(StringComparer.Ordinal)
        {
            ["/data/search-index.json"] = """
                [["a gnoll pup (5)","n",99001],["a kobold worker (6)","n",99002],["a gnoll guard (10)","n",99003]]
                """,
            // One shard (id/1000 == 99 for all three): a gnoll in a non-city hunting zone, a
            // kobold in a different non-city zone, and a gnoll in a *city* — which must never
            // reach a hunt report's zones, only its citiesLeftOut count (Decision 6). The gnoll
            // pup's factionHits exercise the standing/projection wiring end to end.
            ["/data/npcs/99.json"] = """
                {"99001":{"id":99001,"name":"a gnoll pup","level":5,"race":"Gnoll","faction":"Blackburrow Gnolls","respawn":300,
                  "zones":[{"zone":"blackburrow","longName":"Blackburrow","spawnPoints":10,"locs":[[0,0,0]]}],
                  "factionHits":[["Deepwater Knights",-10]]},
                 "99002":{"id":99002,"name":"a kobold worker","level":6,"race":"Kobold","respawn":600,
                  "zones":[{"zone":"kithicor","longName":"Kithicor Forest","spawnPoints":5,"locs":[[0,0,0]]}]},
                 "99003":{"id":99003,"name":"a gnoll guard","level":10,"race":"Gnoll","respawn":120,
                  "zones":[{"zone":"qeynos2","longName":"North Qeynos","spawnPoints":8,"locs":[[0,0,0]]}]}}
                """,
        };

        public string Name => "FakeBase";

        public string HomeUrl => "https://example.invalid";

        public string NpcUrl(int id) => $"https://example.invalid/npcs/{id}/";

        public Task<ReferenceFetch> GetAsync(string path, string? etag, CancellationToken ct)
        {
            Requested.Add(path);
            return Task.FromResult(Bodies.TryGetValue(path, out var body)
                ? ReferenceFetch.Fetched(body, "\"etag-" + path.GetHashCode() + "\"")
                : ReferenceFetch.NotFound());
        }
    }

    private async Task<(WebApplication App, HttpClient Http)> StartAppAsync(FakeSource source, bool noReference = false)
    {
        // All ten redirect flags, pointed at this test's own temp dir — never the real
        // %AppData%\EQDeeps — and every valueless switch last on the list (CLAUDE.md Sec3): a
        // switch eats the token after it, which is how --storeRoot would silently stop protecting
        // anything if a switch were slipped in ahead of it.
        var args = new List<string>
        {
            "--urls", "http://127.0.0.1:0",
            "--recentLogsRoot", _dir,
            "--sampleLogRoot", _dir,
            "--updateRoot", _dir,
            "--mobRoot", _dir,
            "--attackRoot", _dir,
            "--itemRoot", _dir,
            "--referenceRoot", _dir,
            "--storeRoot", _dir,
            "--mapRoot", _dir,
            "--cacheRoot", _dir,
        };
        if (noReference)
        {
            args.Add("--no-reference");
        }

        args.Add("--no-update-check");

        var app = ServerApp.Build(args.ToArray(), reference: source);
        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

    private async Task<string> OpenAsync(HttpClient http, string character, string server)
    {
        var logs = Path.Combine(InstallRoot, "Logs");
        Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, $"eqlog_{character}_{server}.txt");
        await File.WriteAllTextAsync(path, "");

        var response = await http.PostAsJsonAsync("/api/sessions", new { path });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private void WriteAchievements(string character, string server, string text)
    {
        Directory.CreateDirectory(InstallRoot);
        File.WriteAllText(AchievementExport.PathFor(InstallRoot, character, server), text);
    }

    private void WriteFactions(string character, string server, string classCode, string text)
    {
        Directory.CreateDirectory(InstallRoot);
        File.WriteAllText(Path.Combine(InstallRoot, $"{character}_{server}-{classCode}-Factions.txt"), text);
    }

    /// <summary>
    /// One achievement, two open counted components — "Gnolls" (3/10, joins to the Gnoll race) and
    /// "Kobolds" (1/5, joins to Kobold) — so a hunt query can ask for either race alone (component=)
    /// or both united (no component).
    /// </summary>
    private static string AchievementsText() => string.Join('\n',
    [
        "Slayer: Conquest",
        Line("I", "Multi Hunt"),
        Line("I", "", "Gnolls", "3/10"),
        Line("I", "", "Kobolds", "1/5"),
    ]);

    /// <summary>Polls the atlas status until the walk finishes — bounded, never a raw sleep.</summary>
    private static async Task WaitForAtlasAsync(HttpClient http)
    {
        for (var i = 0; i < 200; i++)
        {
            var status = await http.GetFromJsonAsync<JsonElement>("/api/reference/atlas");
            if (status.GetProperty("complete").GetBoolean())
            {
                return;
            }

            await Task.Delay(15);
        }

        throw new TimeoutException("atlas walk did not complete within the test's budget");
    }

    [Fact]
    public async Task RankedZonesMatchAHandComputedExpectation_AndComponentNarrowsTheResult()
    {
        var source = new FakeSource();
        var (app, http) = await StartAppAsync(source);
        await using var appDisposer = app;
        using var httpDisposer = http;

        var id = await OpenAsync(http, "Tester", "testserver");
        WriteAchievements("Tester", "testserver", AchievementsText());
        WriteFactions("Tester", "testserver", "WAR", string.Join('\n',
        [
            Line("ID", "Name", "StandingValue", "PointsToMax"),
            Line("1", "Deepwater Knights", "500", "1500"),
        ]));

        // Opening the panel is the ask: nothing ranks until this POST lands.
        (await http.PostAsync("/api/reference/atlas/start", content: null)).EnsureSuccessStatusCode();
        await WaitForAtlasAsync(http);

        // -- no component: both races united. Remaining = (10-3) + (5-1) = 11 (hand-computed). --
        var union = await http.GetFromJsonAsync<JsonElement>(
            $"/api/sessions/{id}/slayer/hunt?key={Uri.EscapeDataString(AchievementKey)}");

        Assert.Equal(AchievementKey, union.GetProperty("key").GetString());
        Assert.Equal("Multi Hunt", union.GetProperty("title").GetString());
        Assert.Equal("Gnolls, Kobolds", union.GetProperty("creatures").GetString());
        Assert.Equal(11, union.GetProperty("remaining").GetInt32());
        Assert.Equal(1, union.GetProperty("citiesLeftOut").GetInt32()); // the qeynos2 gnoll
        Assert.True(union.GetProperty("atlas").GetProperty("complete").GetBoolean());
        Assert.True(union.GetProperty("factions").GetProperty("found").GetBoolean());
        // No maxLevel was given, and the server applies no default (ADR-023 Decision 4): the field
        // is omitted on the wire (ConfigureJson's WhenWritingNull), not present-and-null.
        Assert.False(union.TryGetProperty("maxLevel", out _));

        var terms = union.GetProperty("terms").EnumerateArray().ToDictionary(
            t => t.GetProperty("term").GetString()!,
            t => t.GetProperty("races").EnumerateArray().Select(r => r.GetString()).ToList());
        Assert.Equal(["Gnoll"], terms["Gnolls"]);
        Assert.Equal(["Kobold"], terms["Kobolds"]);

        var unionZones = union.GetProperty("zones").EnumerateArray().ToList();
        Assert.Equal(2, unionZones.Count);
        Assert.DoesNotContain(unionZones, z => z.GetProperty("shortName").GetString() == "qeynos2");

        // blackburrow: 1 gnoll, spawnPoints 10, respawn 300s, chance defaults to 100 ->
        // 10 * 1.0 * 3600 / 300 = 120/hour. Ranked ahead of kithicor (30/hour) since both are
        // equally "recommended" (no protected-faction loss) and PerHour breaks the tie.
        var blackburrow = unionZones[0];
        Assert.Equal("blackburrow", blackburrow.GetProperty("shortName").GetString());
        Assert.Equal(120, blackburrow.GetProperty("perHour").GetDouble(), 3);
        Assert.Equal(10, blackburrow.GetProperty("spawnPoints").GetInt32());
        Assert.True(blackburrow.GetProperty("recommended").GetBoolean());

        var kithicor = unionZones[1];
        Assert.Equal("kithicor", kithicor.GetProperty("shortName").GetString());
        Assert.Equal(30, kithicor.GetProperty("perHour").GetDouble(), 3);

        // Deepwater Knights: a single mob's hit is the zone's whole supply-weighted mean, so
        // perKill == that mob's own delta (-10). standing 500, remaining 11 ->
        // projected = 500 + round(-10 * 11) = 390.
        var effect = Assert.Single(blackburrow.GetProperty("factions").EnumerateArray());
        Assert.Equal("Deepwater Knights", effect.GetProperty("faction").GetString());
        Assert.Equal(-10, effect.GetProperty("perKill").GetDouble(), 3);
        Assert.False(effect.GetProperty("protected").GetBoolean()); // no "Untapped Potential" names it
        Assert.Equal(500, effect.GetProperty("standing").GetInt32());
        Assert.Equal(390, effect.GetProperty("projected").GetInt32());
        Assert.Empty(kithicor.GetProperty("factions").EnumerateArray());

        // -- component=0 (Gnolls only): only blackburrow. Remaining = 10 - 3 = 7. --
        var gnollsOnly = await http.GetFromJsonAsync<JsonElement>(
            $"/api/sessions/{id}/slayer/hunt?key={Uri.EscapeDataString(AchievementKey)}&component=0");

        Assert.Equal("Gnolls", gnollsOnly.GetProperty("creatures").GetString());
        Assert.Equal(7, gnollsOnly.GetProperty("remaining").GetInt32());
        Assert.Equal(1, gnollsOnly.GetProperty("citiesLeftOut").GetInt32());
        var gnollZone = Assert.Single(gnollsOnly.GetProperty("zones").EnumerateArray());
        Assert.Equal("blackburrow", gnollZone.GetProperty("shortName").GetString());
        var gnollEffect = Assert.Single(gnollZone.GetProperty("factions").EnumerateArray());
        Assert.Equal(430, gnollEffect.GetProperty("projected").GetInt32()); // 500 + round(-10*7)

        // -- component=1 (Kobolds only): only kithicor, and no city touches this race at all. --
        var kobolds = await http.GetFromJsonAsync<JsonElement>(
            $"/api/sessions/{id}/slayer/hunt?key={Uri.EscapeDataString(AchievementKey)}&component=1");

        Assert.Equal("Kobolds", kobolds.GetProperty("creatures").GetString());
        Assert.Equal(4, kobolds.GetProperty("remaining").GetInt32()); // 5 - 1
        Assert.Equal(0, kobolds.GetProperty("citiesLeftOut").GetInt32());
        var kobZone = Assert.Single(kobolds.GetProperty("zones").EnumerateArray());
        Assert.Equal("kithicor", kobZone.GetProperty("shortName").GetString());
    }

    [Fact]
    public async Task AnUnknownAchievementKeyIs404()
    {
        var source = new FakeSource();
        var (app, http) = await StartAppAsync(source);
        await using var appDisposer = app;
        using var httpDisposer = http;

        var id = await OpenAsync(http, "Tester", "testserver");
        WriteAchievements("Tester", "testserver", AchievementsText());

        var response = await http.GetAsync($"/api/sessions/{id}/slayer/hunt?key=nonsense");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownSessionIs404()
    {
        var source = new FakeSource();
        var (app, http) = await StartAppAsync(source);
        await using var appDisposer = app;
        using var httpDisposer = http;

        var response = await http.GetAsync(
            $"/api/sessions/does-not-exist/slayer/hunt?key={Uri.EscapeDataString(AchievementKey)}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task NoFactionFileMeansFoundFalseAndNullStandings_WithEverythingElseIntact()
    {
        var source = new FakeSource();
        var (app, http) = await StartAppAsync(source);
        await using var appDisposer = app;
        using var httpDisposer = http;

        var id = await OpenAsync(http, "Tester", "testserver");
        WriteAchievements("Tester", "testserver", AchievementsText());
        // Deliberately no `*-Factions.txt` written.

        (await http.PostAsync("/api/reference/atlas/start", content: null)).EnsureSuccessStatusCode();
        await WaitForAtlasAsync(http);

        var report = await http.GetFromJsonAsync<JsonElement>(
            $"/api/sessions/{id}/slayer/hunt?key={Uri.EscapeDataString(AchievementKey)}");

        var factions = report.GetProperty("factions");
        Assert.False(factions.GetProperty("found").GetBoolean());
        Assert.False(factions.TryGetProperty("path", out _)); // omitted on the wire, not null (WhenWritingNull)
        Assert.Equal(FactionExport.Command, factions.GetProperty("command").GetString());

        // Everything else still works: two zones, the same faction effect, just with no standing
        // or projection to show.
        var zones = report.GetProperty("zones").EnumerateArray().ToList();
        Assert.Equal(2, zones.Count);
        var effect = Assert.Single(zones[0].GetProperty("factions").EnumerateArray());
        Assert.Equal(-10, effect.GetProperty("perKill").GetDouble(), 3);
        Assert.False(effect.TryGetProperty("standing", out _));
        Assert.False(effect.TryGetProperty("projected", out _));
    }

    [Fact]
    public async Task NoReferenceMeansAnEmptyOkAnswerNeverAnError()
    {
        var source = new FakeSource();
        var (app, http) = await StartAppAsync(source, noReference: true);
        await using var appDisposer = app;
        using var httpDisposer = http;

        var id = await OpenAsync(http, "Tester", "testserver");
        WriteAchievements("Tester", "testserver", AchievementsText());

        var response = await http.GetAsync(
            $"/api/sessions/{id}/slayer/hunt?key={Uri.EscapeDataString(AchievementKey)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(report.GetProperty("atlas").GetProperty("enabled").GetBoolean());
        Assert.Empty(report.GetProperty("zones").EnumerateArray());
        Assert.Equal(0, report.GetProperty("citiesLeftOut").GetInt32());
        Assert.Empty(source.Requested); // --no-reference: the store never asked it anything
    }
}
