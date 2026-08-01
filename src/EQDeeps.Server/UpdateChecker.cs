using System.Reflection;
using System.Text.Json;

namespace EQDeeps.Server;

public sealed record VersionInfo(
    string Version,
    bool UpdateAvailable,
    string? LatestVersion,
    string? ReleaseUrl);

/// <summary>
/// Checks GitHub Releases for a newer tag — the app's only outbound call,
/// explicitly sanctioned by the architecture doc (no telemetry, no
/// auto-install; the UI just shows a link). Failures are silent: an offline
/// machine runs identically.
/// </summary>
public sealed class UpdateChecker
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/Moonchopper/EQDeeps/releases/latest";

    public static readonly string CurrentVersion =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    private volatile VersionInfo _info = new(CurrentVersion, false, null, null);

    public VersionInfo Info => _info;

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EQDeeps/" + CurrentVersion);
            var json = await http.GetStringAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString();
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            if (tag is null)
            {
                return;
            }

            _info = new VersionInfo(CurrentVersion, IsNewer(tag, CurrentVersion), tag.TrimStart('v'), url);
        }
        catch (Exception)
        {
            // Offline, rate-limited, repo private — all fine; stay on "no update".
        }
    }

    /// <summary>True when release tag <paramref name="tag"/> is newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(string tag, string current)
    {
        return TryParse(tag, out var latest) && TryParse(current, out var mine) && latest > mine;

        static bool TryParse(string text, out Version version)
        {
            version = new Version(0, 0);
            var core = text.Trim().TrimStart('v', 'V');
            var end = core.IndexOfAny(['-', '+']);
            if (end >= 0)
            {
                core = core[..end]; // ignore prerelease/build metadata
            }

            if (!core.Contains('.'))
            {
                core += ".0";
            }

            return Version.TryParse(core, out version!);
        }
    }
}
