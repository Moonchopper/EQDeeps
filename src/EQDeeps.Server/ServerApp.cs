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

        var app = builder.Build();

        // Serve the built SPA when present (ui/ builds into wwwroot).
        var hasSpa = File.Exists(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html"));
        if (hasSpa)
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }

        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

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

        if (hasSpa)
        {
            app.MapFallbackToFile("index.html");
        }

        return app;
    }
}
