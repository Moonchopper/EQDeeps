using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace EQDeeps.Server;

/// <summary>
/// The application shell: a native window hosting the SPA in WebView2 (the
/// Chromium engine that ships with Windows 10/11). Keeps the backend/SPA
/// split an implementation detail — the user sees one windowed app with its
/// own icon and taskbar entry, and closing the window exits (Program wires
/// the lifetime). Window placement persists across runs beside the other
/// user documents in %AppData%\EQDeeps.
/// </summary>
internal sealed class AppWindow : Form
{
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly Uri _appUri;
    private readonly Action _onBrowserFallback;

    public AppWindow(string url, Action onBrowserFallback)
    {
        _appUri = new Uri(url);
        _onBrowserFallback = onBrowserFallback;
        Text = "EQDeeps";
        StartPosition = FormStartPosition.Manual;
        ApplyPlacement(LoadPlacement());
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        }
        catch (Exception)
        {
            // No usable exe icon (unusual host): the default form icon will do.
        }

        Controls.Add(_webView);
    }

    /// <summary>
    /// The Evergreen WebView2 runtime ships with Windows 10/11 but can be
    /// absent on stripped-down installs; callers degrade to browser mode.
    /// </summary>
    public static bool IsRuntimeAvailable()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void OpenInDefaultBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser handler — the URL printed to the console is enough.
        }
    }

    /// <summary>Restore and activate; callable from any thread (focus endpoint).</summary>
    public bool TryFocus()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return false;
        }

        try
        {
            BeginInvoke(() =>
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    WindowState = FormWindowState.Normal;
                }

                Activate();
            });
            return true;
        }
        catch (InvalidOperationException)
        {
            return false; // torn down between the check and the invoke
        }
    }

    /// <summary>Close from any thread (host shutting down, e.g. Ctrl+C).</summary>
    public void RequestClose()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(Close);
        }
        catch (InvalidOperationException)
        {
        }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            // Chromium profile data is machine-local cache, not roaming config.
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EQDeeps", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
            await _webView.EnsureCoreWebView2Async(env);
            var core = _webView.CoreWebView2!;
            core.Settings.IsStatusBarEnabled = false;
            // Anything that leaves the local app (GitHub release notes, docs)
            // opens in the user's real browser; this window only shows the app.
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                OpenInDefaultBrowser(args.Uri);
            };
            core.NavigationStarting += (_, args) =>
            {
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) && !IsSameOrigin(target))
                {
                    args.Cancel = true;
                    OpenInDefaultBrowser(args.Uri);
                }
            };
            core.Navigate(_appUri.ToString());
        }
        catch (Exception)
        {
            // Runtime reported available but initialization failed (corrupt
            // install, full disk): degrade to the browser, not a dead window.
            _onBrowserFallback();
            OpenInDefaultBrowser(_appUri.ToString());
            Close();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SavePlacement();
        base.OnFormClosing(e);
    }

    private bool IsSameOrigin(Uri target) =>
        target.Scheme == _appUri.Scheme && target.Host == _appUri.Host && target.Port == _appUri.Port;

    // ---- Window placement persistence ------------------------------------

    private sealed record Placement(int X, int Y, int Width, int Height, bool Maximized);

    private static string PlacementPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQDeeps", "window.json");

    private void ApplyPlacement(Placement? saved)
    {
        var restored = saved is null
            ? Rectangle.Empty
            : new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
        // Reject stale bounds that no longer land on a screen (monitor
        // unplugged, resolution change) so the window can't come back lost.
        var visible = restored.Width >= 400 && restored.Height >= 300 &&
            Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(Rectangle.Inflate(restored, -40, -40)));
        Bounds = visible ? restored : CenteredDefault();
        if (saved?.Maximized == true)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    private static Rectangle CenteredDefault()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 1000);
        var width = Math.Min(1280, area.Width - 80);
        var height = Math.Min(800, area.Height - 80);
        return new Rectangle(
            area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
    }

    private static Placement? LoadPlacement()
    {
        try
        {
            return File.Exists(PlacementPath)
                ? JsonSerializer.Deserialize<Placement>(File.ReadAllText(PlacementPath))
                : null;
        }
        catch (Exception)
        {
            return null; // corrupt or unreadable: fall back to defaults
        }
    }

    private void SavePlacement()
    {
        try
        {
            var normal = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var placement = new Placement(
                normal.X, normal.Y, normal.Width, normal.Height,
                WindowState == FormWindowState.Maximized);
            Directory.CreateDirectory(Path.GetDirectoryName(PlacementPath)!);
            File.WriteAllText(PlacementPath, JsonSerializer.Serialize(placement));
        }
        catch (Exception)
        {
            // Losing window placement is not worth failing shutdown over.
        }
    }
}
