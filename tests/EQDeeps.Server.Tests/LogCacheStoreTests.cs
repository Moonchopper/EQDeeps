using System.Net.Http.Json;
using System.Text.Json;
using EQDeeps.Core.Cache;
using EQDeeps.TestSupport;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// The parsed-record cache store (issue #59, ADR-018): where a log's cache
/// lives, that the app writes one after backfill and reads it back on the
/// next open, and that <c>--cacheRoot</c> keeps all of that out of
/// %AppData%.
/// </summary>
public sealed class LogCacheStoreTests : IAsyncLifetime
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
            "--referenceRoot", _dir,
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

    private static string Line(int t, string action) => SyntheticLogGenerator.Prefix(T0.AddSeconds(t)) + action;

    private async Task<JsonElement> OpenAndBackfillAsync(string path)
    {
        var response = await _http.PostAsJsonAsync("/api/sessions", new { path });
        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = info.GetProperty("id").GetString()!;
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
    public void PathIsStableCaseInsensitiveAndPerBuild()
    {
        var store = new LogCacheStore(_dir);
        var a = store.PathFor(Path.Combine(_dir, "eqlog_Kizant_xegony.txt"));
        var b = store.PathFor(Path.Combine(_dir, "EQLOG_KIZANT_XEGONY.TXT"));
        var c = store.PathFor(Path.Combine(_dir, "eqlog_Kizant_bristlebane.txt"));
        var d = store.PathFor(Path.Combine(_dir, "eqlog_Kizant_xegony.txt"), Guid.NewGuid());
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d); // another parser build keeps its own
        Assert.StartsWith(Path.Combine(_dir, "cache"), a);
        Assert.EndsWith(".eqdc", a);
    }

    [Fact]
    public void ArchivesAndHeldFilesGetNoCache()
    {
        var store = new LogCacheStore(_dir);
        Assert.Null(store.Open(Path.Combine(_dir, "eqlog_Kizant_xegony.txt.gz"), emuMode: false));

        var log = Path.Combine(_dir, "eqlog_Kizant_xegony.txt");
        File.WriteAllLines(log, [Line(0, "An ice giant died.")]);
        using var first = store.Open(log, emuMode: false);
        Assert.NotNull(first);
        Assert.Null(store.Open(log, emuMode: false));
    }

    [Fact]
    public void SweepDropsOrphansOldCachesAndAllButTheNewestForeignBuild()
    {
        var store = new LogCacheStore(_dir);
        var live = Path.Combine(_dir, "eqlog_Live_xegony.txt");
        var gone = Path.Combine(_dir, "eqlog_Gone_xegony.txt");
        var old = Path.Combine(_dir, "eqlog_Old_xegony.txt");
        var release = Guid.NewGuid();   // the installed build, say
        var devA = Guid.NewGuid();      // two earlier dev builds of the same log
        var devB = Guid.NewGuid();
        void Write(string log, Guid build, DateTime written)
        {
            if (!File.Exists(log))
            {
                File.WriteAllLines(log, [Line(0, "An ice giant died.")]);
            }

            var path = store.PathFor(log, build);
            using (var cache = LogCache.Open(path, log, emuMode: false, build))
            {
                cache.Commit(new CacheCheckpoint(new FileInfo(log).Length, 0, 0, 0, 0, 0, null));
            }

            File.SetLastWriteTimeUtc(path, written);
        }

        var now = DateTime.UtcNow;
        Write(live, LogCache.CoreVersion, now);
        Write(live, release, now.AddDays(-1));
        Write(live, devA, now.AddDays(-3));
        Write(live, devB, now.AddDays(-2));
        Write(gone, LogCache.CoreVersion, now);
        Write(old, LogCache.CoreVersion, now.AddDays(-90));
        File.Delete(gone);
        File.WriteAllText(Path.Combine(store.Root, "junk.eqdc"), "not a cache");
        File.WriteAllText(Path.Combine(store.Root, "0123456789ABCDEF0123456789ABCDEF.eqdc"), "pre-build-suffix name");

        // Label caches follow the same rule: mine, the newest foreign, and two
        // older foreign ones that go.
        void Labels(Guid build, DateTime written)
        {
            var path = Path.Combine(store.Root, MapLabelCache.FileNameFor(build));
            File.WriteAllText(path, "{}");
            File.SetLastWriteTimeUtc(path, written);
        }

        Labels(LogCache.CoreVersion, now);
        Labels(release, now.AddDays(-1));
        Labels(devA, now.AddDays(-3));
        Labels(devB, now.AddDays(-2));

        // gone, old, junk, the unsuffixed one, the two older dev builds — and
        // the two older dev builds' label caches.
        Assert.Equal(8, store.Sweep());
        Assert.True(File.Exists(Path.Combine(store.Root, MapLabelCache.FileNameFor(LogCache.CoreVersion))));
        Assert.True(File.Exists(Path.Combine(store.Root, MapLabelCache.FileNameFor(release))));
        Assert.False(File.Exists(Path.Combine(store.Root, MapLabelCache.FileNameFor(devA))));
        Assert.False(File.Exists(Path.Combine(store.Root, MapLabelCache.FileNameFor(devB))));
        Assert.True(File.Exists(store.PathFor(live)));
        Assert.True(File.Exists(store.PathFor(live, release)));
        Assert.False(File.Exists(store.PathFor(live, devA)));
        Assert.False(File.Exists(store.PathFor(live, devB)));
        Assert.False(File.Exists(store.PathFor(gone)));
        Assert.False(File.Exists(store.PathFor(old)));
        Assert.False(File.Exists(Path.Combine(store.Root, "junk.eqdc")));
        Assert.False(File.Exists(Path.Combine(store.Root, "0123456789ABCDEF0123456789ABCDEF.eqdc")));

        // Idempotent: a second sweep finds nothing to do.
        Assert.Equal(0, store.Sweep());
    }

    /// <summary>
    /// The whole point, through the real API: open, close, open again — the
    /// second open restores from the cache the first one wrote, and the file
    /// is under the redirect.
    /// </summary>
    [Fact]
    public async Task ASecondOpenRestoresFromTheCacheTheFirstWrote()
    {
        var log = Path.Combine(_dir, "eqlog_Kizant_xegony.txt");
        File.WriteAllLines(log, Enumerable.Range(0, 200).Select(i =>
            Line(i, $"Raider01 crushes an ice giant for {i + 1} points of damage.")));

        var first = await OpenAndBackfillAsync(log);
        Assert.Equal(200, first.GetProperty("recordCount").GetInt32());
        Assert.Equal(0, first.GetProperty("restoredRecords").GetInt64());
        var id = first.GetProperty("id").GetString();

        // Closing checkpoints, then releases the file.
        (await _http.DeleteAsync($"/api/sessions/{id}")).EnsureSuccessStatusCode();
        var cachePath = new LogCacheStore(_dir).PathFor(log);
        Assert.True(File.Exists(cachePath), $"Nothing was written to {cachePath} — is --cacheRoot wired?");

        // Inside the 30 s inactivity window, so the death closes the same fight.
        File.AppendAllLines(log, [Line(200, "An ice giant died.")]);
        var second = await OpenAndBackfillAsync(log);
        Assert.Equal(200, second.GetProperty("restoredRecords").GetInt64());
        Assert.Equal(201, second.GetProperty("recordCount").GetInt32());
        Assert.Equal(1, second.GetProperty("fightCount").GetInt32());

        var fights = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{second.GetProperty("id").GetString()}/fights");
        var fight = Assert.Single(fights.EnumerateArray());
        Assert.True(fight.GetProperty("dead").GetBoolean());
        Assert.Equal(200 * 201 / 2, fight.GetProperty("damageTotal").GetInt64());
    }
}
