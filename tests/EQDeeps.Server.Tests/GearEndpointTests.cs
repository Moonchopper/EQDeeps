using System.Net.Http.Json;
using System.Text.Json;
using EQDeeps.Server;
using EQDeeps.TestSupport;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// The gear path end to end over real Kestrel: a dump appears in the install
/// root beside the log, the watcher notices it, and the endpoint reports it.
///
/// <para>The log is placed in a &lt;root&gt;\Logs directory on purpose — that is
/// how EverQuest lays an install out, and it is how the watcher works out where
/// to look without any configuration from the player.</para>
/// </summary>
public sealed class GearEndpointTests : IAsyncLifetime
{
    // Comfortably in the past: one test asserts a dump written now sorts after
    // the logged fights, which a future-dated log would invert.
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));

    private string _install = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _install = Path.Combine(_dir, "EverQuest Legends");
        Directory.CreateDirectory(Path.Combine(_install, "Logs"));

        _app = ServerApp.Build([
            "--urls", "http://127.0.0.1:0",
            "--recentLogsRoot", _dir,
            "--sampleLogRoot", _dir,
            "--updateRoot", _dir,
            "--gearRoot", _dir,
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

    private string WriteLog() =>
        WriteLogLines(
            SyntheticLogGenerator.Prefix(T0) + "Kizant crushes an ice giant for 100 points of damage.",
            SyntheticLogGenerator.Prefix(T0.AddSeconds(1)) + "An ice giant died.");

    private string WriteLogLines(params string[] lines)
    {
        var path = Path.Combine(_install, "Logs", "eqlog_Kizant_xegony.txt");
        File.WriteAllLines(path, lines);
        return path;
    }

    private void WriteInventory(params string[] equipment)
    {
        var lines = new List<string> { "Location\tName\tID\tCount\tSlots" };
        lines.AddRange(equipment);
        File.WriteAllLines(Path.Combine(_install, "Kizant_xegony-Inventory.txt"), lines);
    }

    private async Task<string> OpenSessionAsync(string path)
    {
        var response = await _http.PostAsJsonAsync("/api/sessions", new { path });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
    }

    /// <summary>Polls the endpoint until <paramref name="done"/> holds, or gives up.</summary>
    private async Task<JsonElement> GearWhenAsync(string id, Func<JsonElement, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        JsonElement gear = default;
        while (DateTime.UtcNow < deadline)
        {
            gear = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/gear");
            if (done(gear))
            {
                return gear;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"gear never satisfied the condition: {gear}");
    }

    private static JsonElement Snapshots(JsonElement gear) => gear.GetProperty("snapshots");

    [Fact]
    public async Task ReportsNoGearButStillSaysWhereToLook()
    {
        var id = await OpenSessionAsync(WriteLog());

        var gear = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/gear");
        var status = gear.GetProperty("status");

        Assert.Empty(Snapshots(gear).EnumerateArray());
        Assert.False(status.GetProperty("hasSnapshot").GetBoolean());

        // The two things a player needs when the command appeared to do
        // nothing: what to type, and exactly where we are watching for it.
        Assert.Equal("/outputfile inventory", status.GetProperty("command").GetString());
        Assert.Equal(
            Path.Combine(_install, "Kizant_xegony-Inventory.txt"),
            status.GetProperty("expectedPath").GetString());

        // One fight has happened with nothing known about the gear behind it.
        Assert.Equal(1, status.GetProperty("fightsSince").GetInt32());
    }

    [Fact]
    public async Task PicksUpADumpWrittenBeforeTheSessionOpened()
    {
        WriteInventory(
            "Head\tSkull-Shaped Barbute +7\t4301\t1\t10",
            "Primary\tShimmering Ruby Stiletto +5\t5820\t1\t10",
            "Primary-Slot7\tRuby (Exaltation)\t9001\t1\t10",
            "General 1\tBackpack\t32601\t1\t8",
            "General 1-Slot1\tWater Flask\t13006\t18\t10");

        var id = await OpenSessionAsync(WriteLog());
        var gear = await GearWhenAsync(id, g => Snapshots(g).GetArrayLength() == 1);

        var snapshot = Snapshots(gear)[0];
        var equipped = snapshot.GetProperty("equipped");
        Assert.Equal(2, equipped.GetArrayLength());

        var primary = equipped.EnumerateArray().Single(i => i.GetProperty("slotKey").GetString() == "Primary#1");
        Assert.Equal("Shimmering Ruby Stiletto", primary.GetProperty("baseName").GetString());
        Assert.Equal(5, primary.GetProperty("plus").GetInt32());
        Assert.Equal("Ruby (Exaltation)",
            primary.GetProperty("augments")[0].GetProperty("name").GetString());

        Assert.Equal(12, snapshot.GetProperty("upgradeScore").GetInt32());   // +7 and +5
        Assert.True(gear.GetProperty("status").GetProperty("hasSnapshot").GetBoolean());
    }

    [Fact]
    public async Task CaptureTimeIsZonelessLocalLikeEveryOtherTimestamp()
    {
        WriteInventory("Primary\tSword +1\t1\t1\t10");
        var id = await OpenSessionAsync(WriteLog());
        var gear = await GearWhenAsync(id, g => Snapshots(g).GetArrayLength() == 1);

        // The dump's timestamp comes from the file's last-write time, which is
        // Local — and would serialise with a UTC offset that no log timestamp
        // carries. Compared against a fight time, or handed back as a query
        // range, that offset silently shifts the whole window.
        var capturedAt = Snapshots(gear)[0].GetProperty("capturedAt").GetString()!;
        Assert.Equal(DateTimeKind.Unspecified, DateTime.Parse(capturedAt).Kind);

        // The dump was written during this test, so it must sort after a fight
        // logged in 2026-08 — both as text (the SPA compares these strings
        // directly) and as parsed time.
        var fights = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/fights");
        var fightTime = fights[0].GetProperty("beginTime").GetString()!;
        Assert.True(string.CompareOrdinal(capturedAt, fightTime) > 0, $"{capturedAt} !> {fightTime}");
        Assert.True(DateTime.Parse(capturedAt) > DateTime.Parse(fightTime));
    }

    [Fact]
    public async Task NoticesAnUpgradeAndNamesTheChange()
    {
        WriteInventory("Primary\tShimmering Ruby Stiletto +2\t5820\t1\t10");
        var id = await OpenSessionAsync(WriteLog());
        await GearWhenAsync(id, g => Snapshots(g).GetArrayLength() == 1);

        // The player upgrades and dumps again.
        WriteInventory("Primary\tShimmering Ruby Stiletto +5\t5820\t1\t10");
        var gear = await GearWhenAsync(id, g => g.GetProperty("changes").GetArrayLength() == 1);

        var change = gear.GetProperty("changes")[0];
        Assert.Equal(3, change.GetProperty("upgradeScoreDelta").GetInt32());

        var slot = change.GetProperty("slots")[0];
        Assert.Equal("upgraded", slot.GetProperty("kind").GetString());
        Assert.Equal("Primary#1", slot.GetProperty("slotKey").GetString());
        Assert.Equal(2, slot.GetProperty("before").GetProperty("plus").GetInt32());
        Assert.Equal(5, slot.GetProperty("after").GetProperty("plus").GetInt32());
    }

    [Fact]
    public async Task ReDumpingUnchangedGearAddsNothing()
    {
        WriteInventory("Primary\tSword +1\t1\t1\t10");
        var id = await OpenSessionAsync(WriteLog());
        await GearWhenAsync(id, g => Snapshots(g).GetArrayLength() == 1);

        // Same gear, new file: a normal thing to do, and it must cost nothing.
        WriteInventory("Primary\tSword +1\t1\t1\t10");
        await Task.Delay(1000);

        var gear = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/gear");
        Assert.Equal(1, Snapshots(gear).GetArrayLength());
        Assert.Empty(gear.GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public async Task UnknownSessionIsNotFound()
    {
        var response = await _http.GetAsync("/api/sessions/nope/gear");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
