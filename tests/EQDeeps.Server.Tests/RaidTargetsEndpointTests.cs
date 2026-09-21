using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// C3 end to end: <c>GET /api/raids/targets</c> needs no session, no network
/// and no store — it serves the embedded roster (<c>RaidTargets.Default</c>)
/// and nothing else. Every store-backed root is still redirected per
/// CLAUDE.md §3, even though this route touches none of them: a test that
/// redirects most of the stores reads as isolated, and the gap is invisible
/// until something writes.
/// </summary>
public sealed class RaidTargetsEndpointTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _app = ServerApp.Build(
        [
            "--urls", "http://127.0.0.1:0",
            "--recentLogsRoot", _dir,
            "--sampleLogRoot", _dir,
            "--updateRoot", _dir,
            "--mobRoot", _dir,
            "--attackRoot", _dir,
            "--itemRoot", _dir,
            "--referenceRoot", _dir,
            "--storeRoot", _dir,
            "--cacheRoot", _dir,
            "--mapRoot", _dir,
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

    [Fact]
    public async Task ServesEveryRowOfTheFileWithNoSessionOpen()
    {
        var response = await _http.GetAsync("/api/raids/targets");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var targets = body.GetProperty("targets").EnumerateArray().ToList();
        Assert.Equal(EQDeeps.Core.Raids.RaidTargets.Default.Targets.Count, targets.Count);
        Assert.True(targets.Count > 0);

        // camelCase (ServerApp.ConfigureJson): "name", not "Name".
        Assert.True(targets[0].TryGetProperty("name", out _));

        var withAlias = targets.Single(t => t.GetProperty("name").GetString() == "Cazic-Thule");
        Assert.Equal(
            new[] { "Cazic Thule" },
            withAlias.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()).ToArray());

        var withoutAlias = targets.Single(t => t.GetProperty("name").GetString() == "Lord Nagafen");
        Assert.Empty(withoutAlias.GetProperty("aliases").EnumerateArray());
        Assert.Equal("Nagafen's Lair", withoutAlias.GetProperty("zone").GetString());
        Assert.Equal("Dragons", withoutAlias.GetProperty("group").GetString());
    }
}
