using System.Net.Http.Json;
using System.Text.Json;
using EQDeeps.Server.Reference;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// <c>GET /api/reference/status</c> and <c>POST /api/reference/refresh</c> end to end (ADR-020
/// Decision 1's amendment, brief "ship the reference snapshot" S7): a fake <see cref="IReferenceSource"/>
/// AND a fake <see cref="IReferenceSnapshot"/>, injected through <c>ServerApp.Build</c>'s test seams,
/// so nothing here — or in the app it drives — ever reaches eqlbase.com.
/// </summary>
public sealed class ReferenceRefreshEndpointTests : IDisposable
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

    private sealed class FakeSnapshot : IReferenceSnapshot
    {
        public DateTime? SnapshotUtc { get; set; } = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        public readonly Dictionary<string, string> Files = new(StringComparer.Ordinal);

        public string? Read(string fileName) => Files.TryGetValue(fileName, out var text) ? text : null;

        public string? EtagFor(string fileName) => null;
    }

    private sealed class FakeSource : IReferenceSource
    {
        public readonly List<string> Requested = [];
        public readonly Dictionary<string, string> Bodies = new(StringComparer.Ordinal);

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

    /// <summary>
    /// All ten redirect flags, pointed at this test's own temp dir — never the real
    /// <c>%AppData%\EQDeeps</c> — with every valueless switch last (CLAUDE.md §3): a switch eats
    /// the token after it, which is how <c>--storeRoot</c> would silently stop protecting anything
    /// if a switch were slipped in ahead of it.
    /// </summary>
    private async Task<(WebApplication App, HttpClient Http)> StartAppAsync(
        FakeSource source, FakeSnapshot snapshot, bool noReference = false)
    {
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

        var app = ServerApp.Build(args.ToArray(), reference: source, snapshot: snapshot);
        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

    private static FakeSnapshot Snapshot() => new()
    {
        Files =
        {
            ["search-index.json"] = """
                [["a gnoll (5)","n",1001]]
                """,
            ["npcs-1.json"] = """
                {"1001":{"id":1001,"name":"a gnoll","level":5,"hp":50,"race":"Gnoll","respawn":300,
                  "zones":[{"zone":"blackburrow","longName":"Blackburrow","spawnPoints":4,"locs":[[0,0,0]]}]}}
                """,
        },
    };

    private static FakeSource RefreshedSource() => new()
    {
        Bodies =
        {
            ["/data/search-index.json"] = """
                [["a gnoll (5)","n",1001],["a bat (3)","n",3001]]
                """,
            ["/data/npcs/1.json"] = """
                {"1001":{"id":1001,"name":"a gnoll","level":5,"hp":80,"race":"Gnoll","respawn":300,
                  "zones":[{"zone":"blackburrow","longName":"Blackburrow","spawnPoints":4,"locs":[[0,0,0]]}]}}
                """,
            ["/data/npcs/3.json"] = """
                {"3001":{"id":3001,"name":"a bat","level":3,"race":"Bat","respawn":300,
                  "zones":[{"zone":"kithicor","longName":"Kithicor Forest","spawnPoints":2,"locs":[[0,0,0]]}]}}
                """,
        },
    };

    /// <summary>Polls <c>/api/reference/status</c> until the refresh finishes — bounded, never a raw sleep.</summary>
    private static async Task<JsonElement> WaitForRefreshAsync(HttpClient http)
    {
        for (var i = 0; i < 200; i++)
        {
            var status = await http.GetFromJsonAsync<JsonElement>("/api/reference/status");

            // finishedUtc is omitted on the wire while null (ConfigureJson's WhenWritingNull), so
            // its presence — not its value — is what "the walk finished" means here.
            if (status.GetProperty("refresh").TryGetProperty("finishedUtc", out _))
            {
                return status;
            }

            await Task.Delay(15);
        }

        throw new TimeoutException("refresh did not finish within the test's budget");
    }

    [Fact]
    public async Task StatusCarriesTheSnapshotDate_AndRefreshRunsToFinishedWithTheExpectedCounts()
    {
        var source = RefreshedSource();
        var snapshot = Snapshot();
        var (app, http) = await StartAppAsync(source, snapshot);
        await using var appDisposer = app;
        using var httpDisposer = http;

        var status = await http.GetFromJsonAsync<JsonElement>("/api/reference/status");
        Assert.Equal("2026-09-21T12:00:00Z", status.GetProperty("snapshotUtc").GetString());
        Assert.False(status.GetProperty("refresh").GetProperty("running").GetBoolean());

        var started = await http.PostAsync("/api/reference/refresh", content: null);
        started.EnsureSuccessStatusCode();

        var finished = await WaitForRefreshAsync(http);
        var refresh = finished.GetProperty("refresh");
        Assert.False(refresh.GetProperty("running").GetBoolean());
        Assert.Equal(3, refresh.GetProperty("filesTotal").GetInt32()); // index + shards 1, 3
        Assert.Equal(3, refresh.GetProperty("filesChecked").GetInt32());
        // This fake always answers 200 regardless of the ETag it was sent, so all three come back
        // "changed": the index (a new bat entry), shard 1 (hp moved), and shard 3 (never fetched
        // before at all).
        Assert.Equal(3, refresh.GetProperty("filesChanged").GetInt32());
        Assert.False(refresh.TryGetProperty("error", out _)); // omitted on the wire, not null (WhenWritingNull)

        Assert.Equal(3, source.Requested.Count);
    }

    [Fact]
    public async Task NoReferenceMeansThePostMakesZeroRequests()
    {
        var source = RefreshedSource();
        var snapshot = Snapshot();
        var (app, http) = await StartAppAsync(source, snapshot, noReference: true);
        await using var appDisposer = app;
        using var httpDisposer = http;

        var response = await http.PostAsync("/api/reference/refresh", content: null);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("refresh").GetProperty("running").GetBoolean());
        Assert.Empty(source.Requested);
    }
}
