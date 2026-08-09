using System.Net.Http.Json;
using System.Text.Json;
using EQDeeps.TestSupport;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// The incoming-damage path end to end over real Kestrel (F26): a log full of
/// swings taken is opened, the server sweeps the closed fights into its attack
/// index on the expiry tick, and both the profile endpoint and the raw feed
/// report what happened.
/// </summary>
public sealed class MobAttackEndpointTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _app = ServerApp.Build([
            "--urls", "http://127.0.0.1:0",
            "--recentLogsRoot", _dir,
            "--sampleLogRoot", _dir,
            "--updateRoot", _dir,
            "--mobRoot", _dir,
            "--attackRoot", _dir,
        ]);
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

    private static string Line(DateTime at, string action) =>
        SyntheticLogGenerator.Prefix(at) + action;

    /// <summary>
    /// A zone entry, a /who that fixes the character's level, then one pull per
    /// minute in which the mob lands two crushes and misses once before dying.
    /// </summary>
    private static IEnumerable<string> Camp(
        string zone, string mob, int count, int level = 55, int hit = 120)
    {
        yield return Line(T0, $"You have entered {zone}.");
        yield return Line(T0.AddSeconds(1), $"[{level} Warrior] Kizant (Human)");

        for (var i = 0; i < count; i++)
        {
            var at = T0.AddMinutes(i + 1);
            yield return Line(at, $"Kizant crushes {mob} for 900 points of damage.");
            yield return Line(at.AddSeconds(1), $"{mob} crushes Kizant for {hit} points of damage.");
            yield return Line(at.AddSeconds(2), $"{mob} crushes Kizant for {hit * 2} points of damage.");
            yield return Line(at.AddSeconds(3), $"{mob} tries to crush Kizant, but misses!");
            yield return Line(at.AddSeconds(4), $"{mob} died.");
        }
    }

    private async Task<string> OpenAsync(params string[] lines)
    {
        var path = Path.Combine(_dir, "eqlog_Kizant_xegony.txt");
        await File.WriteAllLinesAsync(path, lines);

        var response = await _http.PostAsJsonAsync("/api/sessions", new { path });
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var current = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}");
            if (current.GetProperty("backfillComplete").GetBoolean())
            {
                return id;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("backfill did not complete");
    }

    /// <summary>
    /// Profiles are swept on the 1 Hz expiry tick, which only runs once backfill
    /// is done — so the index is a moment behind the fight list by design.
    /// </summary>
    private async Task<JsonElement> WaitForAttacksAsync(string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var report = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/attacks");
            if (report.GetProperty("mobs").GetArrayLength() > 0)
            {
                return report;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("no attack profiles were learned");
    }

    [Fact]
    public async Task LearnsWhatAMobHitsForAndHowOftenItLands()
    {
        var id = await OpenAsync([.. Camp("The City of Guk 1 (Awakened)", "a froglok ton knight", 6)]);
        var report = await WaitForAttacksAsync(id);

        Assert.Equal("xegony", report.GetProperty("server").GetString());
        Assert.Equal("Kizant", report.GetProperty("character").GetString());
        Assert.Equal(55, report.GetProperty("characterLevel").GetInt32());
        Assert.True(report.GetProperty("instanced").GetBoolean());

        var mob = report.GetProperty("mobs").EnumerateArray().Single();
        Assert.Equal(1, mob.GetProperty("difficulty").GetInt32());
        Assert.Equal(55, mob.GetProperty("defenderLevel").GetInt32());
        Assert.Equal(6, mob.GetProperty("fights").GetInt32());
        Assert.Equal(18, mob.GetProperty("swings").GetInt32());  // 3 per pull
        Assert.Equal(12, mob.GetProperty("landed").GetInt32());  // 2 of those land
        Assert.Equal(2160, mob.GetProperty("total").GetInt64()); // 6 * (120 + 240)
        Assert.Equal(180, mob.GetProperty("avgHit").GetDouble());
        Assert.Equal(240, mob.GetProperty("maxHit").GetInt64());
        Assert.Equal(120, mob.GetProperty("minHit").GetInt64());

        // Two of every three swings landed, and the third missed.
        Assert.Equal(200d / 3, mob.GetProperty("hitRate").GetDouble(), 6);
        Assert.Equal(100d / 3, mob.GetProperty("missRate").GetDouble(), 6);

        var skill = mob.GetProperty("skills").EnumerateArray().Single();
        Assert.Equal("Crushes", skill.GetProperty("skill").GetString());
        Assert.False(skill.GetProperty("spell").GetBoolean());
        Assert.Equal(["Kizant"], mob.GetProperty("defenders").EnumerateArray()
            .Select(d => d.GetString()));
    }

    /// <summary>
    /// The same mob at two difficulties hits for different amounts, which is the
    /// whole reason difficulty is read off the zone line — and the same reason
    /// it is in this key too.
    /// </summary>
    [Fact]
    public async Task DifficultySplitsTheProfile()
    {
        // The second camp is shifted an hour on: both blocks are generated from
        // the same clock, and a log whose timestamps go backwards is not a log.
        var lines = Camp("The City of Guk 1 (Awakened)", "a froglok ton knight", 4, hit: 100)
            .Concat(Camp("The City of Guk 4 (Refined)", "a froglok ton knight", 4, hit: 300)
                .Select(l => Shift(l, TimeSpan.FromHours(1))));

        var id = await OpenAsync([.. lines]);
        var report = await WaitForAttacksAsync(id);

        var byDifficulty = report.GetProperty("mobs").EnumerateArray()
            .ToDictionary(m => m.GetProperty("difficulty").GetInt32());
        Assert.Equal(2, byDifficulty.Count);
        Assert.Equal(150, byDifficulty[1].GetProperty("avgHit").GetDouble());
        Assert.Equal(450, byDifficulty[4].GetProperty("avgHit").GetDouble());
    }

    /// <summary>
    /// The raw feed: what hit, for how much, in order — including the swings
    /// that did nothing, which are half of what a death recap is made of.
    /// </summary>
    [Fact]
    public async Task FeedReturnsIncomingSwingsInOrderWithAvoidance()
    {
        var id = await OpenAsync([.. Camp("The City of Guk", "a froglok ton knight", 2)]);

        var feed = await PostFeedAsync(id, new { scope = new { }, limit = 100 });

        Assert.Equal(6, feed.GetProperty("total").GetInt32());
        var hits = feed.GetProperty("hits").EnumerateArray().ToList();
        Assert.Equal(6, hits.Count);
        Assert.All(hits, h => Assert.Equal("A froglok ton knight", h.GetProperty("attacker").GetString()));
        Assert.All(hits, h => Assert.Equal("Kizant", h.GetProperty("defender").GetString()));
        Assert.Equal([120L, 240L, 0L, 120L, 240L, 0L], hits.Select(h => h.GetProperty("amount").GetInt64()));
        Assert.Equal("miss", hits[2].GetProperty("outcome").GetString());
        Assert.Equal("Crushes", hits[2].GetProperty("skill").GetString());
    }

    /// <summary>The tail is the newest, and the total says what it left behind.</summary>
    [Fact]
    public async Task FeedLimitKeepsTheNewestAndSaysHowManyThereWere()
    {
        var id = await OpenAsync([.. Camp("The City of Guk", "a froglok ton knight", 4)]);

        var feed = await PostFeedAsync(id, new { scope = new { }, limit = 2 });

        Assert.Equal(12, feed.GetProperty("total").GetInt32());
        var amounts = feed.GetProperty("hits").EnumerateArray()
            .Select(h => h.GetProperty("amount").GetInt64()).ToList();
        Assert.Equal([240L, 0L], amounts);
    }

    /// <summary>
    /// The players' own damage is not incoming damage. The camp's 900-point
    /// crushes outnumber everything else and must never appear.
    /// </summary>
    [Fact]
    public async Task FeedNeverContainsOutgoingDamage()
    {
        var id = await OpenAsync([.. Camp("The City of Guk", "a froglok ton knight", 3)]);

        var feed = await PostFeedAsync(id, new { scope = new { }, limit = 500 });

        Assert.DoesNotContain(
            feed.GetProperty("hits").EnumerateArray(),
            h => h.GetProperty("attacker").GetString() == "Kizant");
    }

    /// <summary>
    /// "Me" is resolved server-side against whichever log is open, so the
    /// setting travels between characters instead of naming one of them.
    /// </summary>
    [Fact]
    public async Task OwnerOnlyResolvesAgainstTheOpenLog()
    {
        var lines = Camp("The City of Guk", "a froglok ton knight", 2).ToList();
        lines.Add(Line(T0.AddMinutes(3), "A froglok ton knight crushes Vandil for 500 points of damage."));

        var id = await OpenAsync([.. lines]);

        var everyone = await PostFeedAsync(id, new { scope = new { }, limit = 100 });
        Assert.Contains(
            everyone.GetProperty("hits").EnumerateArray(),
            h => h.GetProperty("defender").GetString() == "Vandil");

        var mine = await PostFeedAsync(id, new { scope = new { }, limit = 100, ownerOnly = true });
        Assert.DoesNotContain(
            mine.GetProperty("hits").EnumerateArray(),
            h => h.GetProperty("defender").GetString() == "Vandil");
    }

    [Fact]
    public async Task UnknownSessionIsNotFound()
    {
        var attacks = await _http.GetAsync("/api/sessions/nope/attacks");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, attacks.StatusCode);

        var feed = await _http.PostAsJsonAsync("/api/sessions/nope/hits", new { scope = new { } });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, feed.StatusCode);
    }

    private async Task<JsonElement> PostFeedAsync(string id, object request)
    {
        var response = await _http.PostAsJsonAsync($"/api/sessions/{id}/hits", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Moves a generated line's timestamp so blocks can be concatenated.</summary>
    private static string Shift(string line, TimeSpan by)
    {
        var close = line.IndexOf(']');
        var at = DateTime.ParseExact(
            line[1..close], "ddd MMM dd HH:mm:ss yyyy",
            System.Globalization.CultureInfo.InvariantCulture);
        return SyntheticLogGenerator.Prefix(at + by) + line[(close + 2)..];
    }
}
