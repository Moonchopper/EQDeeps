using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EQDeeps.Core.Achievements;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// Slayer progress over HTTP (F35 / ADR-023 Decision 1): the character's own
/// <c>/outputfile achievements</c> export, read from the install the log lives
/// in and parsed fresh on every request — the F29 pattern exactly, one file
/// this app reads and never keeps a copy of.
/// </summary>
public sealed class SlayerEndpointTests : IAsyncLifetime
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
            "--itemRoot", _dir,
            "--referenceRoot", _dir,
            "--storeRoot", _dir,
            "--mapRoot", _dir,
            "--cacheRoot", _dir,
            "--no-reference",
            "--no-update-check",
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

    // Same reasoning as AchievementExportTests.Line / SlayerTests.Line in EQDeeps.Core.Tests: no
    // literal tab ever typed into source, so an editor cannot silently break the fixture.
    private static string Line(params string[] columns) => string.Join('\t', columns);

    private string InstallRoot => Path.Combine(_dir, "EverQuest Legends");

    /// <summary>
    /// Opens a session against a fake install's log — <c>eqlog_&lt;Char&gt;_&lt;server&gt;.txt</c>
    /// under <c>Logs\</c> — and returns its session id. Session.Path/Character/Server come off the
    /// file name at open time, synchronously, so this route needs no wait for backfill the way the
    /// item endpoints do.
    /// </summary>
    private async Task<string> OpenAsync(string character, string server)
    {
        var logs = Path.Combine(InstallRoot, "Logs");
        Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, $"eqlog_{character}_{server}.txt");
        await File.WriteAllTextAsync(path, "");

        var response = await _http.PostAsJsonAsync("/api/sessions", new { path });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private void WriteExport(string character, string server, string text)
    {
        Directory.CreateDirectory(InstallRoot);
        File.WriteAllText(AchievementExport.PathFor(InstallRoot, character, server), text);
    }

    [Fact]
    public async Task NoExportFileIsFoundFalseWithTheCommandStillNamed()
    {
        var id = await OpenAsync("Tester", "testserver");

        var report = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/slayer");

        Assert.False(report.GetProperty("found").GetBoolean());
        Assert.False(report.TryGetProperty("problem", out _)); // null is omitted on the wire
        Assert.Equal(AchievementExport.Command, report.GetProperty("command").GetString());
        Assert.EndsWith("Tester_testserver-Achievements.txt", report.GetProperty("path").GetString());
        Assert.Equal(0, report.GetProperty("skippedLines").GetInt32());
        Assert.Empty(report.GetProperty("meta").EnumerateArray());
        Assert.Empty(report.GetProperty("kills").EnumerateArray());
    }

    [Fact]
    public async Task AnExportTooLargeToBeOneSaysSoInsteadOfDrawingAnEmptyTracker()
    {
        var id = await OpenAsync("Tester", "testserver");
        // One byte past the cap. Every line is well-formed, so the only thing wrong with this
        // file is its size — which is the only thing the size check may be reacting to.
        var line = Line("I", "An Achievement") + "\r\n";
        WriteExport("Tester", "testserver", string.Concat(Enumerable.Repeat(line, (4 * 1024 * 1024 / line.Length) + 1)));

        var report = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/slayer");

        Assert.False(report.GetProperty("found").GetBoolean());
        Assert.Contains(AchievementExport.Command, report.GetProperty("problem").GetString());
        Assert.Empty(report.GetProperty("kills").EnumerateArray());
    }

    [Fact]
    public async Task AnExportProducesItsKillAndMetaRows()
    {
        var id = await OpenAsync("Tester", "testserver");
        WriteExport("Tester", "testserver", string.Join('\n',
        [
            "Slayer: Conquest",
            Line("I", "Pesticide"),
            Line("I", "", "Roaches", "3/10"),

            "Slayer: General",
            Line("I", "Meta Example"),
            Line("C", "", "Complete the achievement \"Pesticide\""),
            Line("I", "", "Complete the achievement \"Something Else\""),
            Line("I", "", "(Optional) Complete the achievement \"Unmatched Title\""),
        ]));

        var report = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/slayer");

        Assert.True(report.GetProperty("found").GetBoolean());
        Assert.True(report.TryGetProperty("exportedUtc", out _)); // set (non-null) — the file exists
        Assert.Equal(0, report.GetProperty("skippedLines").GetInt32());

        var kills = report.GetProperty("kills").EnumerateArray().ToList();
        var pesticide = Assert.Single(kills);
        Assert.Equal("Pesticide", pesticide.GetProperty("title").GetString());
        var roaches = Assert.Single(pesticide.GetProperty("components").EnumerateArray());
        Assert.Equal(3, roaches.GetProperty("have").GetInt32());
        Assert.Equal(10, roaches.GetProperty("need").GetInt32());

        var meta = Assert.Single(report.GetProperty("meta").EnumerateArray());
        Assert.Equal("Meta Example", meta.GetProperty("title").GetString());
        // Two non-optional references ("Pesticide", "Something Else"); the (Optional) third does
        // not count toward the total — hand-computed per ADR-023's "non-optional only" rule.
        Assert.Equal(2, meta.GetProperty("requiredTotal").GetInt32());
        Assert.Equal(1, meta.GetProperty("requiredDone").GetInt32());
    }

    [Fact]
    public async Task ReExportingWithoutARestartShowsTheNewCount()
    {
        var id = await OpenAsync("Tester", "testserver");
        string ExportWith(string count) => string.Join('\n',
        [
            "Slayer: Conquest",
            Line("I", "Pesticide"),
            Line("I", "", "Roaches", count),
        ]);

        static int HaveOf(JsonElement report) => report.GetProperty("kills").EnumerateArray().ToList()[0]
            .GetProperty("components").EnumerateArray().ToList()[0].GetProperty("have").GetInt32();

        WriteExport("Tester", "testserver", ExportWith("3/10"));
        var first = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/slayer");
        Assert.Equal(3, HaveOf(first));

        // Same request, same session, nothing restarted — only the file's content changed. If the
        // route cached anything this would still read 3.
        WriteExport("Tester", "testserver", ExportWith("5/10"));
        var second = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/slayer");
        Assert.Equal(5, HaveOf(second));
    }

    [Fact]
    public async Task ALogNotUnderAnInstallGivesAnActionableProblem()
    {
        var stray = Path.Combine(_dir, "SomewhereElse");
        Directory.CreateDirectory(stray);
        var path = Path.Combine(stray, "eqlog_Tester_testserver.txt");
        await File.WriteAllTextAsync(path, "");
        var response = await _http.PostAsJsonAsync("/api/sessions", new { path });
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var report = await _http.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/slayer");

        Assert.False(report.GetProperty("found").GetBoolean());
        var problem = report.GetProperty("problem").GetString();
        Assert.False(string.IsNullOrWhiteSpace(problem));
        // Advice, not a null check: it should read as a sentence about the install, not mention
        // the implementation detail that produced it.
        Assert.DoesNotContain("null", problem, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("install", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownSessionIsNotFound()
    {
        var response = await _http.GetAsync("/api/sessions/does-not-exist/slayer");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
