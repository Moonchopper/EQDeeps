using System.Windows.Forms;
using EQDeeps.Server;
using EQDeeps.Server.Updates;
using Microsoft.Extensions.DependencyInjection;

// Launch behavior (feature F14): the exe is a windowed app — start the
// localhost server, then host the SPA in the app's own WebView2 window.
// Closing the window exits (the log is the source of truth — relaunching
// backfills instantly, so nothing is worth orphaning a background process
// for). Start on the default port, fall back to a dynamic port when it's
// taken, and reuse an already running instance (focus its window) instead of
// starting twice. Without the WebView2 runtime, degrade to browser mode:
// open the default browser and exit shortly after the last tab is
// deliberately closed. Flags: --browser (default browser instead of the app
// window), --no-browser (headless: no UI at all), --no-update-check,
// --stay-alive (keep running with no UI open), --urls <url>.

Native.AttachConsole(-1); // WinExe has no console; reattach so terminal launches still print

var browserMode = args.Contains("--browser");
var noUi = args.Contains("--no-browser");
var noUpdateCheck = args.Contains("--no-update-check");
var stayAlive = args.Contains("--stay-alive");
var explicitUrls = args.Any(a => a.StartsWith("--urls", StringComparison.OrdinalIgnoreCase)) ||
                   Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is not null;

var windowMode = !browserMode && !noUi && AppWindow.IsRuntimeAvailable();

if (!explicitUrls && await IsEqdeepsAlreadyRunningAsync(ServerApp.DefaultUrl))
{
    Console.WriteLine($"EQDeeps is already running at {ServerApp.DefaultUrl} — switching to it.");
    if (!noUi)
    {
        // Surface the running instance's window; browser-mode servers (or
        // older builds) have none, so open a tab like before.
        if (browserMode || !await TryFocusRunningInstanceAsync(ServerApp.DefaultUrl))
        {
            AppWindow.OpenInDefaultBrowser(ServerApp.DefaultUrl);
        }
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
Console.WriteLine($"EQDeeps v{AppVersion.Current} — {url}" +
                  (windowMode ? "" : "  (Ctrl+C to quit)"));

var updates = app.Services.GetRequiredService<UpdateService>();
if (!noUpdateCheck)
{
    updates.Start();
}

if (windowMode)
{
    StartWindow(app, url, stayAlive);
}
else if (!noUi)
{
    AppWindow.OpenInDefaultBrowser(url);
}

// Browser tabs govern lifetime only when they are the UI; in window mode the
// window governs it, and headless runs are never auto-exited.
if (!windowMode && !stayAlive)
{
    _ = MonitorUiClientsAsync(app);
}

await app.WaitForShutdownAsync();

// Now that the window is gone and our files are unlocked, hand any staged
// installer to the updater (ADR-010). Deliberately the last thing we do: an
// update never interrupts a parse, it just means the next launch is newer.
if (!noUpdateCheck)
{
    updates.ApplyOnExit();
}

return;

// The shell window runs its message loop on a dedicated STA thread while the
// host keeps the main thread; closing the window stops the host (unless
// --stay-alive), and a stopping host closes the window (e.g. Ctrl+C).
static void StartWindow(WebApplication app, string url, bool stayAlive)
{
    var lifetime = app.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
    var bridge = app.Services.GetRequiredService<WindowBridge>();
    using var ready = new ManualResetEventSlim();
    AppWindow? window = null;
    var uiThread = new Thread(() =>
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var fellBackToBrowser = false;
        using var w = new AppWindow(url, onBrowserFallback: () =>
        {
            // WebView2 died after the window opened; the browser tab just
            // launched is now the UI, so the tab monitor takes over lifetime.
            fellBackToBrowser = true;
            if (!stayAlive)
            {
                _ = MonitorUiClientsAsync(app);
            }
        });
        window = w;
        bridge.Attach(w.TryFocus);
        ready.Set();
        Application.Run(w);
        if (!fellBackToBrowser && !stayAlive)
        {
            lifetime.StopApplication();
        }
    })
    {
        IsBackground = true,
        Name = "EQDeeps UI",
    };
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    ready.Wait();
    lifetime.ApplicationStopping.Register(() => window?.RequestClose());
}

// Once a UI has connected, exit when the last one has been *deliberately*
// closed (pagehide goodbye) and gone past a grace period. Disconnects without
// a goodbye — tab discarded by the browser's memory saver, tab frozen, system
// sleep — leave the server running so the returning tab can reconnect.
// Headless usage never connects a client, so it is never auto-exited.
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
                clients.LastCloseWasDeliberate &&
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

static async Task<bool> TryFocusRunningInstanceAsync(string url)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var response = await http.PostAsync(url + "/api/ui/focus", content: null);
        return response.IsSuccessStatusCode;
    }
    catch (Exception)
    {
        return false;
    }
}

internal static class Native
{
    /// <summary>ATTACH_PARENT_PROCESS = -1; fails harmlessly when double-clicked.</summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    internal static extern bool AttachConsole(int dwProcessId);
}
