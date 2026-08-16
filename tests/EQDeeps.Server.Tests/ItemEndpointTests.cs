using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EQDeeps.TestSupport;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// The item registry over HTTP (F29): the log's loot, sales and purchases
/// meet the client's own item files in one per-server list; a name resolves
/// to the game's id; and the feed lists every mention in a scope, chat
/// included, newest first.
///
/// <para>The log is written under a fake install — <c>&lt;root&gt;\Logs\</c>
/// with <c>userdata\LF_&lt;Char&gt;_&lt;server&gt;.ini</c> beside it — because
/// that is the only way the app is allowed to find the files: read from the
/// install the log lives in, never configured, never copied.</para>
/// </summary>
public sealed class ItemEndpointTests : IAsyncLifetime
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

    private string InstallRoot => Path.Combine(_dir, "EverQuest Legends");

    private async Task<string> OpenAsync(params string[] lines)
    {
        var logs = Path.Combine(InstallRoot, "Logs");
        Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, "eqlog_Kizant_qeynos.txt");
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

    /// <summary>The registry is fed on the 1 Hz tick after backfill, so the first list may be a beat behind.</summary>
    private async Task<JsonElement> WaitForItemsAsync(string id, int atLeast)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        JsonElement report = default;
        while (DateTime.UtcNow < deadline)
        {
            report = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/items");
            if (report.GetProperty("items").GetArrayLength() >= atLeast)
            {
                return report;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"registry never reached {atLeast} items: {report}");
    }

    private void WriteLootFilter(params string[] rows)
    {
        var userdata = Path.Combine(InstallRoot, "userdata");
        Directory.CreateDirectory(userdata);
        File.WriteAllLines(Path.Combine(userdata, "LF_Kizant_qeynos.ini"),
            new[] { "#ITEM_ID^FILTER_ID^ICON_ID^ITEM_NAME" }.Concat(rows));
    }

    [Fact]
    public async Task LogAndClientFilesMeetInOneListAndNamesResolveToIds()
    {
        WriteLootFilter("7352^4^762^Fine Steel Rapier", "13005^4^800^Bone Chips");
        var id = await OpenAsync(
            Line(T0, "You have entered The Ruins of Old Guk."),
            Line(T0.AddMinutes(1), "--You have looted a Fine Steel Rapier +2 from a froglok ton knight's corpse.--"),
            Line(T0.AddMinutes(2), "--Soandso has looted an Ebon Dagger from a dry bone skeleton's corpse.--"),
            Line(T0.AddMinutes(3), "You receive 2 gold 5 silver 9 copper from Didek Stormhammer for the Rusty Two Handed Sword +2(s)."),
            Line(T0.AddMinutes(4), "You purchased 20 Bat Wing from Merchant Rusti for  1 gold 5 copper."));

        var report = await WaitForItemsAsync(id, 5);
        Assert.Equal("qeynos", report.GetProperty("server").GetString());
        var items = report.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("name").GetString()!, i => i);
        Assert.Contains("Fine Steel Rapier", items.Keys);
        Assert.Contains("Ebon Dagger", items.Keys);
        Assert.Contains("Rusty Two Handed Sword", items.Keys);
        Assert.Contains("Bat Wing", items.Keys);
        Assert.Contains("Bone Chips", items.Keys);

        // The loot line and the filter file are one row: the log's sighting, the file's id.
        var rapier = items["Fine Steel Rapier"];
        Assert.Equal(7352, rapier.GetProperty("id").GetInt32());
        Assert.Equal(762, rapier.GetProperty("iconId").GetInt32());
        Assert.Equal(1, rapier.GetProperty("looted").GetInt32());
        Assert.Equal(1, items["Rusty Two Handed Sword"].GetProperty("sold").GetInt32());
        Assert.Equal(20, items["Bat Wing"].GetProperty("bought").GetInt32());
        // Nulls are omitted on the wire, so "no id" is "no property".
        Assert.False(items["Ebon Dagger"].TryGetProperty("id", out var noId) && noId.ValueKind != JsonValueKind.Null);
        Assert.Equal(2, report.GetProperty("numbered").GetInt32());

        // Resolve by any decoration or casing; 204 for a stranger.
        var resolved = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/items/resolve?name=fine%20steel%20rapier%20%2B4");
        Assert.Equal(7352, resolved.GetProperty("id").GetInt32());
        var stranger = await _http.GetAsync($"/api/sessions/{id}/items/resolve?name=Sword%20of%20Nothing");
        Assert.Equal(HttpStatusCode.NoContent, stranger.StatusCode);
    }

    [Fact]
    public async Task TheFeedListsEveryMentionNewestFirstIncludingChat()
    {
        WriteLootFilter("7352^4^762^Fine Steel Rapier");
        var id = await OpenAsync(
            Line(T0, "You have entered The Ruins of Old Guk."),
            Line(T0.AddMinutes(1), "--You have looted a Fine Steel Rapier +2 from a froglok ton knight's corpse.--"),
            Line(T0.AddMinutes(2), "Paith tells NewPlayers1:1, 'go to befallen, you can also get Fine Steel Rapier +6'"),
            Line(T0.AddMinutes(3), "Glubbug tells the group, 'anyone need Bone Chips?'"),
            Line(T0.AddMinutes(4), "You receive 1 platinum from Didek Stormhammer for the Fine Steel Rapier(s)."));

        await WaitForItemsAsync(id, 1);
        var response = await _http.PostAsJsonAsync($"/api/sessions/{id}/items/mentions", new
        {
            scope = new { timeRanges = new[] { new { begin = T0, end = T0.AddMinutes(10) } } },
        });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var mentions = result.GetProperty("mentions").EnumerateArray().ToList();

        // Bone Chips is not a known item on this server, so the group line is not a mention.
        Assert.Equal(3, mentions.Count);
        Assert.Equal(3, result.GetProperty("total").GetInt32());
        Assert.Equal("sold", mentions[0].GetProperty("kind").GetString());
        Assert.Equal("Didek Stormhammer", mentions[0].GetProperty("where").GetString());
        Assert.Equal("chat", mentions[1].GetProperty("kind").GetString());
        Assert.Equal("Paith", mentions[1].GetProperty("who").GetString());
        Assert.Equal("newplayers1", mentions[1].GetProperty("where").GetString()); // the chat grammar lower-cases channel names
        Assert.Equal(7352, mentions[1].GetProperty("id").GetInt32());
        Assert.Equal("looted", mentions[2].GetProperty("kind").GetString());
        Assert.Equal("Fine Steel Rapier", mentions[2].GetProperty("item").GetString());
        Assert.Equal("a froglok ton knight", mentions[2].GetProperty("where").GetString());
    }
}
