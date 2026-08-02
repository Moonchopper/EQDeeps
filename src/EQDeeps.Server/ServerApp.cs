using System.Text.Json;
using System.Text.Json.Serialization;
using EQDeeps.Core.Query;

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
        builder.Services.AddSingleton<UpdateChecker>();
        builder.Services.AddSingleton<ClientTracker>();
        builder.Services.AddSingleton<WindowBridge>();

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

        app.MapGet("/api/version", (UpdateChecker updates) => Results.Ok(updates.Info));

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

        app.MapGet("/api/logs/discovered", () => Results.Ok(LogDiscovery.Discover()));

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
