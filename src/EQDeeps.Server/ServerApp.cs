using System.Text.Json;
using System.Text.Json.Serialization;
using EQDeeps.Core.Query;
using EQDeeps.Server.Updates;

namespace EQDeeps.Server;

/// <summary>
/// Builds the EQDeeps backend host: sessions REST API + SignalR live channel,
/// bound to localhost only (never a network service). Factored out of Program
/// so integration tests can launch the exact production pipeline on a dynamic
/// port.
/// </summary>
public static class ServerApp
{
    public const string DefaultUrl = "http://127.0.0.1:5487";

    public static void ConfigureJson(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    }

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (builder.Configuration["urls"] is null &&
            Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
        {
            builder.WebHost.UseUrls(DefaultUrl);
        }

        builder.Services.ConfigureHttpJsonOptions(o => ConfigureJson(o.SerializerOptions));
        builder.Services.AddSignalR().AddJsonProtocol(o => ConfigureJson(o.PayloadSerializerOptions));
        builder.Services.AddSingleton<SessionManager>();
        builder.Services.AddSingleton<DocumentStore>();
        // Update stack (ADR-010). --updateRoot redirects the preference and
        // staged-installer files the same way --recentLogsRoot does, so tests
        // never touch the real %AppData%.
        builder.Services.AddSingleton(_ => new UpdatePreferenceStore(builder.Configuration["updateRoot"]));
        builder.Services.AddSingleton(_ => new PendingUpdateStore(builder.Configuration["updateRoot"]));
        builder.Services.AddSingleton(_ => new UpdateInstaller());
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddSingleton<ClientTracker>();
        builder.Services.AddSingleton<WindowBridge>();
        // --recentLogsRoot redirects the MRU file (tests); default: %AppData%\EQDeeps.
        builder.Services.AddSingleton(_ => new RecentLogs(builder.Configuration["recentLogsRoot"]));
        // --sampleLogRoot likewise redirects the extracted demo log (tests).
        builder.Services.AddSingleton(_ => new SampleLog(builder.Configuration["sampleLogRoot"]));
        // --gearRoot likewise redirects the gear snapshot history (tests).
        builder.Services.AddSingleton(_ => new GearStore(builder.Configuration["gearRoot"]));

        var app = builder.Build();

        // Serve the built SPA: the physical wwwroot in dev, the copy embedded
        // into this assembly in published builds (single-file exe, no loose files).
        var spa = ResolveSpaProvider(app.Environment.ContentRootPath);
        if (spa is not null)
        {
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = spa });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = spa });
        }

        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

        // Kept in its original shape: the pill in the session bar only needs
        // "am I current", and a stable endpoint means an older SPA cached in
        // WebView2 still renders something sane after an update.
        app.MapGet("/api/version", (UpdateService updates) =>
        {
            var state = updates.State;
            return Results.Ok(new
            {
                version = state.Version,
                updateAvailable = state.LatestVersion is not null &&
                                  AppVersion.IsNewer(state.LatestVersion, state.Version),
                latestVersion = state.LatestVersion,
                releaseUrl = state.ReleaseUrl,
            });
        });

        // ---- update consent (F22 / ADR-010) --------------------------------

        app.MapGet("/api/update/state", (UpdateService updates) => Results.Ok(updates.State));

        // An explicit "check for updates" overrides every standing decline —
        // see UpdatePreferences.Decide.
        app.MapPost("/api/update/check", async (UpdateService updates) =>
        {
            await updates.CheckAsync(userInitiated: true);
            return Results.Ok(updates.State);
        });

        // The user said yes: download and stage it. Installing still waits for
        // them to close the app (or press Restart now).
        app.MapPost("/api/update/stage", async (StageUpdateRequest? request, UpdateService updates) =>
        {
            await updates.StageAsync(request?.ApplyWhenReady ?? false);
            return Results.Ok(updates.State);
        });

        // The three flavours of "no", which differ only in how long they last.
        app.MapPost("/api/update/defer", (DeferUpdateRequest request, UpdateService updates) =>
        {
            switch (request.Scope)
            {
                case DeferScope.Once:
                    updates.DeclineForThisRun();
                    break;
                case DeferScope.Release:
                    updates.SkipOfferedRelease();
                    break;
                case DeferScope.CurrentVersion:
                    updates.MuteForCurrentVersion();
                    break;
                default:
                    return Results.BadRequest(new { error = "unknown defer scope" });
            }

            return Results.Ok(updates.State);
        });

        app.MapPut("/api/update/mode", (SetUpdateModeRequest request, UpdateService updates) =>
        {
            updates.SetMode(request.Mode);
            return Results.Ok(updates.State);
        });

        // Restart now: the one path allowed to raise a UAC prompt, because the
        // user is looking at the button they just pressed.
        app.MapPost("/api/update/apply", (UpdateService updates) =>
            updates.ApplyNow(out var error)
                ? Results.NoContent()
                : Results.BadRequest(new { error = string.IsNullOrEmpty(error) ? "nothing staged" : error }));

        // pagehide beacon from a genuinely closing tab (see ClientTracker).
        app.MapPost("/api/ui/goodbye", (ClientTracker clients) =>
        {
            clients.OnGoodbye();
            return Results.NoContent();
        });

        // A second exe launch asks the running instance to surface its shell
        // window; 404 means there is no window (browser mode, headless, or an
        // older build) and the caller opens a browser tab instead.
        app.MapPost("/api/ui/focus", (WindowBridge bridge) =>
            bridge.TryFocus() ? Results.NoContent() : Results.NotFound());

        // Install-discovery plus the persisted recently-opened list, deduped
        // by path — so previously tracked logs come back even when the game
        // isn't running and discovery alone would find nothing. The bundled
        // demo log is pinned last with source "sample": always available,
        // never competing with (or mistakable for) the player's real logs.
        app.MapGet("/api/logs/discovered", (RecentLogs recents, SampleLog sample) =>
        {
            var results = LogDiscovery.Discover();
            var seen = new HashSet<string>(
                results.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var recent in recents.List())
            {
                if (!seen.Contains(recent) && !sample.IsSamplePath(recent) &&
                    LogDiscovery.Describe(recent, "recent") is { } log)
                {
                    results.Add(log);
                }
            }

            var ordered = results.OrderByDescending(r => r.LastWriteTime).ToList();
            if (sample.TryEnsureExtracted() is { } samplePath &&
                LogDiscovery.Describe(samplePath, "sample") is { } sampleLog)
            {
                ordered.Add(sampleLog);
            }

            return Results.Ok(ordered);
        });

        // Drop one entry from the recently-opened list — logs the player is
        // done with (test files, copies) shouldn't keep being offered. Only the
        // MRU is edited: the file stays, and installed-log discovery will still
        // find it if it lives in a real EverQuest Logs directory.
        app.MapDelete("/api/logs/recent", (string path, RecentLogs recents) =>
            string.IsNullOrWhiteSpace(path) ? Results.BadRequest(new { error = "path is required" })
            : recents.Forget(path) ? Results.NoContent()
            : Results.NotFound(new { error = "not in recent logs", path }));

        app.MapGet("/api/store/{key}", (string key, DocumentStore store) =>
            !DocumentStore.IsValidKey(key)
                ? Results.NotFound()
                : store.Read(key) is { } doc
                    ? Results.Ok(doc)
                    : Results.NoContent());

        app.MapPut("/api/store/{key}", (string key, System.Text.Json.JsonElement body, DocumentStore store) =>
        {
            if (!DocumentStore.IsValidKey(key))
            {
                return Results.NotFound();
            }

            store.Write(key, body);
            return Results.NoContent();
        });

        app.MapGet("/api/sessions", (SessionManager manager) => Results.Ok(manager.List()));

        app.MapPost("/api/sessions", (OpenSessionRequest request, SessionManager manager) =>
        {
            try
            {
                var host = manager.Open(request);
                return Results.Ok(host.Info());
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound(new { error = "log file not found", request.Path });
            }
        });

        app.MapDelete("/api/sessions/{id}", async (string id, SessionManager manager) =>
            await manager.CloseAsync(id) ? Results.NoContent() : Results.NotFound());

        app.MapGet("/api/sessions/{id}", (string id, SessionManager manager) =>
            manager.Get(id) is { } host ? Results.Ok(host.Info()) : Results.NotFound());

        app.MapGet("/api/sessions/{id}/fights", (string id, SessionManager manager) =>
            manager.Get(id) is { } host ? Results.Ok(host.Fights()) : Results.NotFound());

        app.MapPost("/api/sessions/{id}/query", (string id, QuerySpec spec, SessionManager manager) =>
            manager.Get(id) is { } host ? Results.Ok(host.Execute(spec)) : Results.NotFound());

        app.MapPost("/api/sessions/{id}/timeline", (string id, TimelineRequest request, SessionManager manager) =>
            manager.Get(id) is { } host ? Results.Ok(host.Timeline(request)) : Results.NotFound());

        // Where the character was and what level they were, for the strip the
        // charts draw above the plot. Read-only and derived — nothing here is
        // stored, it is the record stream read as two step functions.
        app.MapGet("/api/sessions/{id}/context", (string id, SessionManager manager) =>
            manager.Get(id) is { } host ? Results.Ok(host.Context()) : Results.NotFound());

        // Gear snapshots for this session's character (F24). Read-only: the
        // player writes these by typing /outputfile inventory in game, and the
        // app only ever notices — it never asks the game for anything.
        app.MapGet("/api/sessions/{id}/gear", (string id, SessionManager manager) =>
            manager.Get(id) is { } host ? Results.Ok(host.Gear()) : Results.NotFound());

        app.MapHub<LiveHub>("/hubs/live");

        if (spa is not null)
        {
            app.MapFallback(() => Results.Stream(
                spa.GetFileInfo("index.html").CreateReadStream(), "text/html"));
        }

        return app;
    }

    private static Microsoft.Extensions.FileProviders.IFileProvider? ResolveSpaProvider(string contentRoot)
    {
        var physical = Path.Combine(contentRoot, "wwwroot");
        if (File.Exists(Path.Combine(physical, "index.html")))
        {
            return new Microsoft.Extensions.FileProviders.PhysicalFileProvider(physical);
        }

        try
        {
            var embedded = new Microsoft.Extensions.FileProviders.ManifestEmbeddedFileProvider(
                typeof(ServerApp).Assembly, "wwwroot");
            return embedded.GetFileInfo("index.html").Exists ? embedded : null;
        }
        catch (InvalidOperationException)
        {
            return null; // built without a wwwroot (UI not built yet): API-only
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
