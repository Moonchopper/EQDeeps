using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EQDeeps.Server;
using EQDeeps.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// End-to-end over real Kestrel + real SignalR: open a session on a temp log,
/// backfill, then script appends to the file and verify pushed updates arrive —
/// the phase-5 exit criterion is a sub-250 ms file-append→client-update path.
/// </summary>
public sealed class ServerIntegrationTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private string _baseUrl = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _app = ServerApp.Build(["--urls", "http://127.0.0.1:0"]);
        await _app.StartAsync();
        _baseUrl = _app.Urls.First();
        _http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
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

    private static string Line(int t, string action) => SyntheticLogGenerator.Prefix(T0.AddSeconds(t)) + action;

    private string WriteLog(params string[] lines)
    {
        var path = Path.Combine(_dir, "eqlog_Kizant_xegony.txt");
        File.WriteAllLines(path, lines);
        return path;
    }

    private async Task<JsonElement> OpenSessionAsync(string path)
    {
        var response = await _http.PostAsJsonAsync("/api/sessions", new { path });
        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = info.GetProperty("id").GetString()!;

        // Wait for backfill (bounded).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var current = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}");
            if (current.GetProperty("backfillComplete").GetBoolean())
            {
                return current;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("backfill did not complete");
    }

    [Fact]
    public async Task HealthAndSessionLifecycle()
    {
        var health = await _http.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.Equal("ok", health.GetProperty("status").GetString());

        var path = WriteLog(
            Line(0, "Raider01 crushes an ice giant for 100 points of damage."),
            Line(1, "An ice giant died."));
        var info = await OpenSessionAsync(path);

        Assert.Equal("Kizant", info.GetProperty("character").GetString());
        Assert.Equal("xegony", info.GetProperty("server").GetString());
        Assert.Equal(2, info.GetProperty("recordCount").GetInt32());

        var id = info.GetProperty("id").GetString();
        var fights = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/fights");
        var fight = Assert.Single(fights.EnumerateArray());
        Assert.Equal("An ice giant", fight.GetProperty("name").GetString());
        Assert.True(fight.GetProperty("dead").GetBoolean());
        Assert.Equal(100, fight.GetProperty("damageTotal").GetInt64());

        var missing = await _http.PostAsJsonAsync("/api/sessions", new { path = Path.Combine(_dir, "nope.txt") });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);

        var close = await _http.DeleteAsync($"/api/sessions/{id}");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, close.StatusCode);
        Assert.Empty((await _http.GetFromJsonAsync<JsonElement>("/api/sessions")).EnumerateArray());
    }

    [Fact]
    public async Task QueryEndpointExecutesSpecs()
    {
        var path = WriteLog(
            Line(0, "Raider01 crushes an ice giant for 100 points of damage."),
            Line(1, "Raider01 crushes an ice giant for 200 points of damage."),
            Line(2, "Raider02 kicks an ice giant for 50 points of damage."));
        var info = await OpenSessionAsync(path);
        var id = info.GetProperty("id").GetString();

        var response = await _http.PostAsJsonAsync($"/api/sessions/{id}/query", new
        {
            source = "damage",
            scope = new { },
            groupBy = new[] { "player" },
            metrics = new[] { "total", "dps" },
        });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(350, result.GetProperty("totals").GetProperty("total").GetDouble());
        var rows = result.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal("Raider01", rows[0].GetProperty("key").GetString());
        Assert.Equal(300, rows[0].GetProperty("metrics").GetProperty("total").GetDouble());
    }

    [Fact]
    public async Task LiveAppendsReachSubscribedClientsUnderLatencyBudget()
    {
        var path = WriteLog(Line(0, "An ice giant died."));
        var info = await OpenSessionAsync(path);
        var id = info.GetProperty("id").GetString()!;

        await using var connection = new HubConnectionBuilder()
            .WithUrl(_baseUrl + "/hubs/live")
            .Build();

        var ticks = new System.Collections.Concurrent.BlockingCollection<JsonElement>();
        connection.On<JsonElement>("tick", ticks.Add);
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe", id);

        await using var appendStream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await using var writer = new StreamWriter(appendStream, Encoding.Latin1) { AutoFlush = true };

        // Scripted appends — no EverQuest anywhere. Measure append → push.
        var latencies = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var amount = 100 + i;
            var sw = Stopwatch.StartNew();
            writer.WriteLine(Line(10 + i, $"Raider01 crushes a shadow drake for {amount} points of damage."));

            var got = false;
            while (!got && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (ticks.TryTake(out var tick, TimeSpan.FromSeconds(5)))
                {
                    var total = tick.GetProperty("result").GetProperty("totals").GetProperty("total").GetDouble();
                    if (total >= 100 * (i + 1)) // cumulative fight total includes this hit
                    {
                        sw.Stop();
                        latencies.Add(sw.Elapsed.TotalMilliseconds);
                        got = true;
                    }
                }
            }

            Assert.True(got, $"append {i} never produced a tick");
            await Task.Delay(30); // separate the samples
        }

        latencies.Sort();
        var median = latencies[latencies.Count / 2];
        Assert.True(median < 250, $"median append→push latency {median:F0} ms (all: {string.Join(", ", latencies.Select(l => l.ToString("F0")))})");
    }

    [Fact]
    public async Task FightListPushesOnChange()
    {
        var path = WriteLog(Line(0, "An ice giant died."));
        var info = await OpenSessionAsync(path);
        var id = info.GetProperty("id").GetString()!;

        await using var connection = new HubConnectionBuilder()
            .WithUrl(_baseUrl + "/hubs/live")
            .Build();
        var fightsPushes = new System.Collections.Concurrent.BlockingCollection<JsonElement>();
        connection.On<JsonElement>("fights", fightsPushes.Add);
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe", id);

        await using var appendStream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await using var writer = new StreamWriter(appendStream, Encoding.Latin1) { AutoFlush = true };
        writer.WriteLine(Line(60, "Raider01 crushes a shadow drake for 500 points of damage."));

        // The very first push after subscribing may predate the append (e.g. the
        // initial empty snapshot) — consume pushes until the new fight shows up.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var found = false;
        while (!found && DateTime.UtcNow < deadline &&
               fightsPushes.TryTake(out var push, TimeSpan.FromSeconds(5)))
        {
            found = push.GetProperty("fights").EnumerateArray()
                .Any(f => f.GetProperty("name").GetString() == "A shadow drake");
        }

        Assert.True(found, "no fights push contained the new fight");
    }
}
