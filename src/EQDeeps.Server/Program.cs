using System.Diagnostics;
using EQDeeps.Server;
using Microsoft.Extensions.DependencyInjection;

// Launch behavior (feature F14): start on the default localhost port, fall
// back to a dynamic port when it's taken by something else, reuse an already
// running EQDeeps instead of starting twice, and open the default browser.
// Flags: --no-browser, --no-update-check, --urls <url> (standard ASP.NET).

var noBrowser = args.Contains("--no-browser");
var noUpdateCheck = args.Contains("--no-update-check");
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

await app.WaitForShutdownAsync();
return;

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
