using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// Pointing the app at a maps folder (F27) — for the machine that has the logs
/// but not the game, or an install somewhere discovery does not walk.
///
/// <para>Deliberately started <em>without</em> <c>--mapRoot</c>, because that
/// flag outranks the user's setting and would make these tests prove nothing.
/// The assertions are all about the nominated folder replacing discovery
/// outright, so they hold whether or not this machine has EverQuest on it.</para>
/// </summary>
public sealed class MapRootTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));

    private string _maps = "";
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _maps = Path.Combine(_dir, "somewhere", "maps");
        Directory.CreateDirectory(_maps);
        File.WriteAllText(
            Path.Combine(_maps, "gfaydark.txt"),
            "L 0, 0, 0, 10, 10, 0, 64, 64, 64\nP 1, 2, 0, 0, 0, 240, 3, to_Clan_Crushbone");

        _app = ServerApp.Build([
            "--urls", "http://127.0.0.1:0",
            "--recentLogsRoot", _dir,
            "--sampleLogRoot", _dir,
            "--updateRoot", _dir,
            "--mobRoot", _dir,
            "--attackRoot", _dir,
            "--storeRoot", _dir,
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

    private Task<HttpResponseMessage> SetRoot(string? path) =>
        _http.PostAsJsonAsync("/api/maps/root", new { path });

    [Fact]
    public async Task NominatedFolderReplacesDiscovery()
    {
        var response = await SetRoot(_maps);
        response.EnsureSuccessStatusCode();

        var catalog = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(catalog.GetProperty("found").GetBoolean());
        Assert.Equal(_maps, catalog.GetProperty("userRoot").GetString());

        // Exactly the synthetic zone — proof discovery was bypassed rather
        // than added to, on a machine that may well have EverQuest installed.
        var zone = Assert.Single(catalog.GetProperty("zones").EnumerateArray());
        Assert.Equal("gfaydark", zone.GetProperty("shortName").GetString());
    }

    [Fact]
    public async Task TheChoiceSurvivesIntoTheStoreAndTheNextRead()
    {
        await SetRoot(_maps);

        var catalog = await _http.GetFromJsonAsync<JsonElement>("/api/maps");
        Assert.Equal(_maps, catalog.GetProperty("userRoot").GetString());

        // It is the user's document, not a private file — same place their
        // dashboards live, because it is a correction they made.
        var stored = await File.ReadAllTextAsync(Path.Combine(_dir, "map-settings.json"));
        Assert.Contains("root", stored);
    }

    [Fact]
    public async Task ClearingGoesBackToDiscovery()
    {
        await SetRoot(_maps);
        var response = await SetRoot(null);
        response.EnsureSuccessStatusCode();

        var catalog = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(catalog.TryGetProperty("userRoot", out var root) && root.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task RejectsAPathThatIsNotThere()
    {
        var response = await SetRoot(Path.Combine(_dir, "no-such-folder"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("no folder", body.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsAFolderWithNoMapsInIt()
    {
        var empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);

        var response = await SetRoot(empty);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("no everquest map files", body.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picking the install folder instead of the maps folder inside it is the
    /// obvious mistake, and the fix is one directory away — so it is worth
    /// naming rather than answering with the generic "no maps here".
    /// </summary>
    [Fact]
    public async Task NamesTheMistakeWhenPointedAtTheInstallFolder()
    {
        var response = await SetRoot(Path.Combine(_dir, "somewhere"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("install folder", body.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A rejected path must not have moved anything: the previous setting, and
    /// the catalog built from it, stay exactly as they were.
    /// </summary>
    [Fact]
    public async Task ARejectedPathLeavesTheWorkingOneAlone()
    {
        await SetRoot(_maps);
        await SetRoot(Path.Combine(_dir, "no-such-folder"));

        var catalog = await _http.GetFromJsonAsync<JsonElement>("/api/maps");

        Assert.Equal(_maps, catalog.GetProperty("userRoot").GetString());
        Assert.Single(catalog.GetProperty("zones").EnumerateArray());
    }
}
