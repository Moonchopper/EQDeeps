using System.Diagnostics;
using EQDeeps.Server;
using Microsoft.Extensions.DependencyInjection;

// Launch behavior (feature F14): start on the default localhost port, fall
// back to a dynamic port when it's taken by something else, reuse an already
// running EQDeeps instead of starting twice, open the default browser, and
// exit shortly after the last browser tab closes (the log is the source of
// truth — reopening backfills instantly, so nothing is worth orphaning a
// background process for). Flags: --no-browser, --no-update-check,
// --stay-alive (keep running with no UI connected), --urls <url>.

var noBrowser = args.Contains("--no-browser");
var noUpdateCheck = args.Contains("--no-update-check");
var stayAlive = args.Contains("--stay-alive");
var explicitUrls = args.Any(a => a.StartsWith("--urls", StringComparison.OrdinalIgnoreCase)) ||
                   Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is not null;

if (!explicitUrls && await IsEqdeepsAlreadyRunningAsync(ServerApp.DefaultUrl))
{
    Console.WriteLine($"EQDeeps is already running at {ServerApp.DefaultUrl} — opening it.");
    if (!noBrowser)
    {
        OpenBrowser(ServerApp.DefaultUrl);
    }

    return;
}

var app = ServerApp.Build(args);
try
{
    await app.StartAsync();
}
catch (IOException) when (!explicitUrls)
{
    // Default port owned by some other program: let Kestrel pick a free one.
    await app.DisposeAsync();
    app = ServerApp.Build([.. args, "--urls", "http://127.0.0.1:0"]);
    await app.StartAsync();
}

var url = app.Urls.FirstOrDefault() ?? ServerApp.DefaultUrl;
Console.WriteLine($"EQDeeps v{UpdateChecker.CurrentVersion} — {url}  (Ctrl+C to quit)");

if (!noUpdateCheck)
{
    _ = app.Services.GetRequiredService<UpdateChecker>().CheckAsync();
}

if (!noBrowser)
{
    OpenBrowser(url);
}

if (!stayAlive)
{
    _ = MonitorUiClientsAsync(app);
}

await app.WaitForShutdownAsync();
return;

// Once a UI has connected, exit when the last one has been gone for a grace
// period (long enough for refreshes and reconnects). Headless usage never
// connects a client, so it is never auto-exited.
static async Task MonitorUiClientsAsync(WebApplication app)
{
    var clients = app.Services.GetRequiredService<ClientTracker>();
    var lifetime = app.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
    var grace = TimeSpan.FromSeconds(10);
    try
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(lifetime.ApplicationStopping))
        {
            if (clients.EverConnected && clients.Count == 0 &&
                DateTime.UtcNow - clients.LastDisconnectUtc > grace)
            {
                Console.WriteLine(
                    "Browser closed — exiting. (Run with --stay-alive to keep parsing without a UI.)");
                lifetime.StopApplication();
                return;
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
}

static async Task<bool> IsEqdeepsAlreadyRunningAsync(string url)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var response = await http.GetAsync(url + "/api/health");
        return response.IsSuccessStatusCode;
    }
    catch (Exception)
    {
        return false;
    }
}

static void OpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", url);
        }
        else
        {
            Process.Start("xdg-open", url);
        }
    }
    catch (Exception)
    {
        // No browser available — the printed URL is enough.
    }
}
