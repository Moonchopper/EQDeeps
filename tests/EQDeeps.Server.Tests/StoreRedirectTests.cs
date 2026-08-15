using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// Proves <c>--storeRoot</c> actually moves the user's documents.
///
/// <para>This is a regression test for a real incident rather than a
/// completeness exercise. <see cref="DocumentStore"/> always accepted a root,
/// but it was the one store never wired to configuration — so no flag could
/// redirect it, while five sibling flags made a harness look isolated. A UI
/// test driving the real SPA overwrote a real dashboard, because App.tsx PUTs
/// <c>dashboards</c> during its load migration and a PUT replaces the whole
/// document.</para>
///
/// <para>What it costs to be wrong here is the point: mob health and attack
/// profiles are caches that relearn, but dashboards and saved queries are the
/// user's own work and there is no history to recover them from.</para>
/// </summary>
public sealed class StoreRedirectTests : IAsyncLifetime
{
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

    /// <summary>
    /// The written document has to land under the redirect. Without the flag
    /// wired it lands in the real %AppData% instead — which is not something a
    /// test can safely assert against, so it asserts the positive: the file is
    /// here, with these contents.
    /// </summary>
    [Fact]
    public async Task WritesDocumentsUnderTheRedirectedRoot()
    {
        var response = await _http.PutAsJsonAsync(
            "/api/store/dashboards",
            new { dashboards = new[] { new { id = "d1", name = "Only a test" } } });

        response.EnsureSuccessStatusCode();

        var path = Path.Combine(_dir, "dashboards.json");
        Assert.True(File.Exists(path), $"Nothing was written to {path} — is --storeRoot wired?");
        Assert.Contains("Only a test", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ReadsBackWhatItWrote()
    {
        await _http.PutAsJsonAsync("/api/store/ui-settings", new { petRollup = true });

        var read = await _http.GetFromJsonAsync<JsonElement>("/api/store/ui-settings");

        Assert.True(read.GetProperty("petRollup").GetBoolean());
    }

    /// <summary>
    /// The key allowlist is what stops a stray key writing an arbitrary file
    /// into the store directory, redirected or not. Only three keys exist:
    /// dashboards, saved-queries, ui-settings.
    /// </summary>
    [Fact]
    public async Task RefusesKeysOutsideTheAllowlist()
    {
        var response = await _http.PutAsJsonAsync("/api/store/notakey", new { x = 1 });

        Assert.False(response.IsSuccessStatusCode);
        Assert.False(File.Exists(Path.Combine(_dir, "notakey.json")));
    }

    /// <summary>
    /// A traversal has to be sent percent-encoded to reach the route at all —
    /// <see cref="HttpClient"/> collapses <c>/api/store/../evil</c> to
    /// <c>/api/evil</c> before it leaves the process, so the obvious spelling
    /// of this test silently checks nothing.
    /// </summary>
    [Fact]
    public async Task RefusesAKeyThatTriesToEscapeTheStoreDirectory()
    {
        var response = await _http.PutAsJsonAsync("/api/store/%2E%2E%2Fevil", new { x = 1 });

        Assert.False(response.IsSuccessStatusCode);

        var escaped = Path.Combine(Path.GetDirectoryName(_dir)!, "evil.json");
        Assert.False(File.Exists(escaped), $"Wrote outside the store root: {escaped}");
    }
}
