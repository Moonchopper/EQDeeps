using System.Net.Http.Json;
using System.Text.Json;
using EQDeeps.Server;
using EQDeeps.TestSupport;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// The mob-health path end to end over real Kestrel (F25): a log full of kills
/// is opened, the server sweeps them into its index on the expiry tick, and
/// both the mob endpoint and the fight list report what was learned.
/// </summary>
public sealed class MobHealthEndpointTests : IAsyncLifetime
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
            "--itemRoot", _dir,
            "--storeRoot", _dir,
            "--cacheRoot", _dir,
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
    /// A zone entry followed by one kill per minute — spaced past the
    /// concurrency window, so none of them look like a pull that had two mobs
    /// up and the merged-fight filter keeps them all.
    /// </summary>
    /// <param name="startMinute">
    /// Where this block sits on the log's clock. Blocks are concatenated, and
    /// a log whose timestamps go backwards is not a log.
    /// </param>
    private static IEnumerable<string> Kills(
        string zone, string mob, long damage, int count, int startMinute = 0)
    {
        var start = T0.AddMinutes(startMinute);
        yield return Line(start, $"You have entered {zone}.");
        for (var i = 0; i < count; i++)
        {
            var at = start.AddMinutes(i + 1);
            yield return Line(at, $"Kizant crushes {mob} for {damage} points of damage.");
            yield return Line(at.AddSeconds(1), $"{mob} died.");
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
    /// Kills are swept on the 1 Hz expiry tick, which only runs once backfill
    /// is done — so the index is a moment behind the fight list by design.
    /// </summary>
    private async Task<JsonElement> WaitForMobsAsync(string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var report = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/mobs");
            if (report.GetProperty("mobs").GetArrayLength() > 0)
            {
                return report;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("no mobs were learned");
    }

    [Fact]
    public async Task LearnsHealthFromKillsAndReportsItPerDifficulty()
    {
        var lines = Kills("The City of Guk 1 (Awakened)", "a froglok ton knight", 800, 6)
            .Concat(Kills("The City of Guk 4 (Refined)", "a froglok ton knight", 1800, 6, startMinute: 10))
            .ToArray();

        var id = await OpenAsync(lines);
        var report = await WaitForMobsAsync(id);

        Assert.Equal("xegony", report.GetProperty("server").GetString());
        Assert.True(report.GetProperty("instanced").GetBoolean());

        var mobs = report.GetProperty("mobs").EnumerateArray()
            .ToDictionary(m => m.GetProperty("difficulty").GetInt32());
        Assert.Equal(2, mobs.Count);
        Assert.Equal(800, mobs[1].GetProperty("health").GetInt64());
        Assert.Equal(1800, mobs[4].GetProperty("health").GetInt64());
        Assert.Equal("The City of Guk", mobs[4].GetProperty("zone").GetString());
        Assert.Equal("Refined", mobs[4].GetProperty("tierName").GetString());
    }

    /// <summary>
    /// The fight list's column: once the mob is known, every fight against it
    /// carries the estimate beside its own damage, which is what makes "was
    /// that a whole kill" answerable.
    /// </summary>
    [Fact]
    public async Task FightsCarryTheLearnedHealthAndTheirDifficulty()
    {
        var id = await OpenAsync([.. Kills("The City of Guk 4 (Refined)", "a froglok ton knight", 1800, 6)]);
        await WaitForMobsAsync(id);

        var fights = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/fights");
        var fight = fights.EnumerateArray().Last();
        Assert.Equal(4, fight.GetProperty("difficulty").GetInt32());
        Assert.Equal(1800, fight.GetProperty("estimatedHealth").GetInt64());
        Assert.Equal(1800, fight.GetProperty("damageTotal").GetInt64());
    }

    /// <summary>
    /// The open world says nothing about difficulty, and neither the fight nor
    /// the estimate should invent one.
    /// </summary>
    [Fact]
    public async Task OpenWorldKillsCarryNoDifficulty()
    {
        var id = await OpenAsync([.. Kills("The City of Guk", "a froglok ton knight", 350, 6)]);
        var report = await WaitForMobsAsync(id);

        Assert.False(report.GetProperty("instanced").GetBoolean());
        var mob = report.GetProperty("mobs").EnumerateArray().Single();
        // Nulls are omitted from responses, so "no difficulty" is an absent
        // property rather than a null one.
        Assert.False(mob.TryGetProperty("difficulty", out _));
        Assert.Equal(350, mob.GetProperty("health").GetInt64());
    }
}
