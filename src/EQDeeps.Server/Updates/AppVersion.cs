using System.Reflection;

namespace EQDeeps.Server.Updates;

/// <summary>
/// The running build's version, and the ordering rule used to decide whether a
/// published release is newer than it. Split out from the update machinery so
/// the comparison stays trivially testable and has no dependency on NetSparkle.
/// </summary>
public static class AppVersion
{
    public static readonly string Current =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>True when release <paramref name="candidate"/> is newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        return TryParse(candidate, out var latest) && TryParse(current, out var mine) && latest > mine;
    }

    /// <summary>
    /// Lenient parse of the shapes releases actually carry: "v1.2.3", "1.2.3",
    /// "1.2.3-beta.1", "1.2" — prerelease and build metadata are dropped, so a
    /// prerelease never sorts above the release it precedes. That is deliberate:
    /// this app has no prerelease channel, and treating "1.3.0-rc1" as equal to
    /// "1.3.0" is far safer than offering a downgrade.
    /// </summary>
    public static bool TryParse(string text, out Version version)
    {
        version = new Version(0, 0);
        var core = text.Trim().TrimStart('v', 'V');
        var end = core.IndexOfAny(['-', '+']);
        if (end >= 0)
        {
            core = core[..end];
        }

        if (!core.Contains('.'))
        {
            core += ".0";
        }

        return Version.TryParse(core, out version!);
    }

    /// <summary>Normalized form used as the key for skip/mute preferences.</summary>
    public static string Normalize(string text) =>
        TryParse(text, out var parsed) ? parsed.ToString() : text.Trim().TrimStart('v', 'V');
}
