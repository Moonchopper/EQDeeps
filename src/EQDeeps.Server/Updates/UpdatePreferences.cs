namespace EQDeeps.Server.Updates;

/// <summary>How much consent the user wants before a release is installed.</summary>
public enum UpdateMode
{
    /// <summary>Ask before staging each new release. The default (F22).</summary>
    Ask,

    /// <summary>Stage every new release silently; the user is only told it's ready.</summary>
    Auto,

    /// <summary>Never check on our own — the user checks by hand from the UI.</summary>
    Manual,
}

/// <summary>What the update loop should do about a release it just found.</summary>
public enum UpdateAction
{
    /// <summary>Nothing: no release, or the user has already answered for this one.</summary>
    None,

    /// <summary>Show the consent dialog and wait for an answer.</summary>
    Prompt,

    /// <summary>Download and stage without asking (auto mode, or consent already given).</summary>
    Stage,
}

/// <summary>
/// The user's standing answers about updating, persisted across runs. Three
/// independent levers, because "no" means three different things in practice:
///
///   <list type="bullet">
///   <item><see cref="SkippedVersions"/> — "not this release" (snooze until a
///   newer one ships). Keyed by the release being offered.</item>
///   <item><see cref="MutedOnVersion"/> — "stop asking while I'm on this build".
///   Keyed by the version the user is <em>running</em>, so prompts resume by
///   themselves once they do update.</item>
///   <item><see cref="Mode"/> — the standing policy.</item>
///   </list>
///
/// The fourth lever, "not right now", is deliberately <em>not</em> here: it
/// lives in memory for the life of the process (see <c>UpdateService</c>), so
/// restarting the app or checking by hand brings the offer back.
/// </summary>
public sealed record UpdatePreferences
{
    public UpdateMode Mode { get; init; } = UpdateMode.Ask;

    /// <summary>Releases the user asked not to be re-offered, normalized.</summary>
    public IReadOnlyList<string> SkippedVersions { get; init; } = [];

    /// <summary>The running version the user muted prompts on, normalized.</summary>
    public string? MutedOnVersion { get; init; }

    public static UpdatePreferences Default { get; } = new();

    public UpdatePreferences Skip(string release) =>
        this with
        {
            SkippedVersions = SkippedVersions
                .Append(AppVersion.Normalize(release))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                // Old entries are harmless but unbounded over years of releases;
                // only recent answers can still match a live offer.
                .TakeLast(20)
                .ToArray(),
        };

    public UpdatePreferences MuteOn(string runningVersion) =>
        this with { MutedOnVersion = AppVersion.Normalize(runningVersion) };

    /// <summary>
    /// Decides what to do about <paramref name="release"/>. Pure, so the whole
    /// consent matrix is unit-testable without a network or a clock.
    /// </summary>
    /// <param name="release">The release on offer, or null if none was found.</param>
    /// <param name="current">The version currently running.</param>
    /// <param name="declinedThisRun">Releases answered "not right now" since launch.</param>
    /// <param name="userInitiated">
    /// True when the user pressed "check for updates". That is an explicit ask,
    /// so it overrides every standing "don't tell me" — otherwise a user who
    /// once muted prompts could never find an update again without editing JSON.
    /// </param>
    public UpdateAction Decide(
        string? release,
        string current,
        IReadOnlySet<string> declinedThisRun,
        bool userInitiated = false)
    {
        if (release is null || !AppVersion.IsNewer(release, current))
        {
            return UpdateAction.None;
        }

        if (userInitiated)
        {
            return UpdateAction.Prompt;
        }

        if (Mode == UpdateMode.Manual)
        {
            return UpdateAction.None;
        }

        if (Mode == UpdateMode.Auto)
        {
            return UpdateAction.Stage;
        }

        var offered = AppVersion.Normalize(release);
        if (SkippedVersions.Contains(offered, StringComparer.OrdinalIgnoreCase) ||
            declinedThisRun.Contains(offered) ||
            string.Equals(MutedOnVersion, AppVersion.Normalize(current), StringComparison.OrdinalIgnoreCase))
        {
            return UpdateAction.None;
        }

        return UpdateAction.Prompt;
    }
}
