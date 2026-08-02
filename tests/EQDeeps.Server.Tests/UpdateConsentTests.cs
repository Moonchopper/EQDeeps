using EQDeeps.Server.Updates;
using Xunit;

namespace EQDeeps.Server.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("v0.2.0", "0.1.0", true)]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("v1.0.0", "0.9.9", true)]
    [InlineData("v0.1.0", "0.1.0", false)]
    [InlineData("v0.1.0", "0.2.0", false)]
    [InlineData("v0.2.0-rc.1", "0.1.0", true)]
    [InlineData("v0.1.0+build5", "0.1.0", false)]
    [InlineData("garbage", "0.1.0", false)]
    [InlineData("v2", "1.0.0", true)]
    public void IsNewerComparesSemverishTags(string tag, string current, bool expected)
    {
        Assert.Equal(expected, AppVersion.IsNewer(tag, current));
    }

    [Theory]
    [InlineData("v0.3.2", "0.3.2")]
    [InlineData("0.3.2", "0.3.2")]
    [InlineData("V0.3.2-beta", "0.3.2")]
    public void NormalizeStripsPrefixAndPrerelease(string input, string expected)
    {
        Assert.Equal(expected, AppVersion.Normalize(input));
    }

    /// <summary>
    /// A prerelease must not read as newer than the release it precedes —
    /// otherwise an "0.4.0-rc1" tag would offer a downgrade to 0.4.0 users.
    /// </summary>
    [Fact]
    public void PrereleaseDoesNotOutrankItsRelease()
    {
        Assert.False(AppVersion.IsNewer("v0.4.0-rc1", "0.4.0"));
    }
}

/// <summary>
/// Guards the one build-time mistake that disables auto-update without failing
/// anything: shipping the placeholder public key. The app degrades safely to
/// notify-only in that case, but silently — a log line is the only other signal.
/// </summary>
public class ReleaseKeyTests
{
    [Fact]
    public void ReleasePublicKeyIsARealEd25519Key()
    {
        Assert.False(
            UpdateService.PublicKey.StartsWith("REPLACE_WITH", StringComparison.Ordinal),
            "The Ed25519 placeholder is still in place — auto-update would silently " +
            "disable itself. See docs/release-signing.md.");

        // Ed25519 public keys are exactly 32 bytes; anything else would leave
        // Ed25519Checker without a signer and fail every verification.
        Assert.Equal(32, Convert.FromBase64String(UpdateService.PublicKey).Length);
    }
}

/// <summary>
/// The consent matrix (F22). These are the four levers the user actually sees,
/// so each one is pinned here rather than left to the service to get right.
/// </summary>
public class UpdateConsentTests
{
    private const string Current = "0.3.2";
    private const string Offered = "0.4.0";

    private static readonly IReadOnlySet<string> NothingDeclined = new HashSet<string>();

    [Fact]
    public void AsksByDefaultWhenAReleaseIsNewer()
    {
        Assert.Equal(
            UpdateAction.Prompt,
            UpdatePreferences.Default.Decide(Offered, Current, NothingDeclined));
    }

    [Fact]
    public void SaysNothingWhenTheReleaseIsNotNewer()
    {
        Assert.Equal(
            UpdateAction.None,
            UpdatePreferences.Default.Decide("0.3.2", Current, NothingDeclined));
        Assert.Equal(
            UpdateAction.None,
            UpdatePreferences.Default.Decide(null, Current, NothingDeclined));
    }

    [Fact]
    public void AutoModeStagesWithoutAsking()
    {
        var preferences = UpdatePreferences.Default with { Mode = UpdateMode.Auto };
        Assert.Equal(UpdateAction.Stage, preferences.Decide(Offered, Current, NothingDeclined));
    }

    [Fact]
    public void ManualModeNeverActsOnItsOwn()
    {
        var preferences = UpdatePreferences.Default with { Mode = UpdateMode.Manual };
        Assert.Equal(UpdateAction.None, preferences.Decide(Offered, Current, NothingDeclined));
    }

    // ---- lever 1: "not right now" ----------------------------------------

    [Fact]
    public void DecliningForThisRunSuppressesOnlyThatRelease()
    {
        var declined = new HashSet<string> { "0.4.0" };
        Assert.Equal(UpdateAction.None, UpdatePreferences.Default.Decide(Offered, Current, declined));
        // A later release is a fresh question.
        Assert.Equal(UpdateAction.Prompt, UpdatePreferences.Default.Decide("0.5.0", Current, declined));
    }

    // ---- lever 2: "skip this release" ------------------------------------

    [Fact]
    public void SkippedReleaseIsNotOfferedAgain()
    {
        var preferences = UpdatePreferences.Default.Skip("v0.4.0");
        Assert.Equal(UpdateAction.None, preferences.Decide(Offered, Current, NothingDeclined));
    }

    [Fact]
    public void SkippingOneReleaseStillAllowsTheNextOne()
    {
        var preferences = UpdatePreferences.Default.Skip("0.4.0");
        Assert.Equal(UpdateAction.Prompt, preferences.Decide("0.4.1", Current, NothingDeclined));
    }

    [Fact]
    public void SkipIsStoredNormalizedSoTagPrefixesMatch()
    {
        var preferences = UpdatePreferences.Default.Skip("v0.4.0");
        Assert.Contains("0.4.0", preferences.SkippedVersions);
        // The offer may arrive with or without the "v".
        Assert.Equal(UpdateAction.None, preferences.Decide("v0.4.0", Current, NothingDeclined));
    }

    [Fact]
    public void SkipListStaysBounded()
    {
        var preferences = UpdatePreferences.Default;
        for (var i = 0; i < 40; i++)
        {
            preferences = preferences.Skip($"1.0.{i}");
        }

        Assert.True(preferences.SkippedVersions.Count <= 20);
        // The most recent answers are the ones that can still match an offer.
        Assert.Contains("1.0.39", preferences.SkippedVersions);
    }

    // ---- lever 3: "don't ask again on this build" -------------------------

    [Fact]
    public void MutingSilencesEveryReleaseWhileOnThatBuild()
    {
        var preferences = UpdatePreferences.Default.MuteOn(Current);
        Assert.Equal(UpdateAction.None, preferences.Decide(Offered, Current, NothingDeclined));
        Assert.Equal(UpdateAction.None, preferences.Decide("9.9.9", Current, NothingDeclined));
    }

    /// <summary>
    /// The mute is keyed to the version the user was running when they set it,
    /// so it expires by itself the moment they end up on something newer —
    /// no way to permanently silence updates by accident.
    /// </summary>
    [Fact]
    public void MutingExpiresOnceTheUserIsOnANewerBuild()
    {
        var preferences = UpdatePreferences.Default.MuteOn("0.3.2");
        Assert.Equal(UpdateAction.Prompt, preferences.Decide("0.5.0", "0.4.0", NothingDeclined));
    }

    // ---- lever 4: an explicit check overrides everything ------------------

    [Theory]
    [InlineData(UpdateMode.Ask)]
    [InlineData(UpdateMode.Manual)]
    public void UserInitiatedCheckIgnoresEveryStandingDecline(UpdateMode mode)
    {
        var preferences = UpdatePreferences.Default with { Mode = mode };
        preferences = preferences.Skip(Offered).MuteOn(Current);
        var declined = new HashSet<string> { "0.4.0" };

        Assert.Equal(
            UpdateAction.Prompt,
            preferences.Decide(Offered, Current, declined, userInitiated: true));
    }

    [Fact]
    public void UserInitiatedCheckStillReportsNothingWhenCurrent()
    {
        Assert.Equal(
            UpdateAction.None,
            UpdatePreferences.Default.Decide("0.1.0", Current, NothingDeclined, userInitiated: true));
    }
}

public class UpdatePreferenceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eqdeeps-update-prefs-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultsToAskingWhenNothingIsStored()
    {
        var store = new UpdatePreferenceStore(_root);
        Assert.Equal(UpdateMode.Ask, store.Read().Mode);
        Assert.Empty(store.Read().SkippedVersions);
    }

    [Fact]
    public void RoundTripsThroughDisk()
    {
        new UpdatePreferenceStore(_root)
            .Write(UpdatePreferences.Default with { Mode = UpdateMode.Auto });

        var reloaded = new UpdatePreferenceStore(_root).Read();
        Assert.Equal(UpdateMode.Auto, reloaded.Mode);
    }

    [Fact]
    public void UpdateAppliesAndPersists()
    {
        var store = new UpdatePreferenceStore(_root);
        store.Update(p => p.Skip("v0.4.0").MuteOn("0.3.2"));

        var reloaded = new UpdatePreferenceStore(_root).Read();
        Assert.Contains("0.4.0", reloaded.SkippedVersions);
        Assert.Equal("0.3.2", reloaded.MutedOnVersion);
    }

    [Fact]
    public void CorruptFileFallsBackToAsking()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "update-prefs.json"), "{ not json");

        Assert.Equal(UpdateMode.Ask, new UpdatePreferenceStore(_root).Read().Mode);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

public class PendingUpdateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eqdeeps-pending-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReadsNothingWhenNothingStaged()
    {
        Assert.Null(new PendingUpdateStore(_root).Read());
    }

    [Fact]
    public void RoundTripsAStagedInstaller()
    {
        var installer = Path.Combine(_root, "EQDeeps-Setup.exe");
        Directory.CreateDirectory(_root);
        File.WriteAllText(installer, "not really an installer");

        var store = new PendingUpdateStore(_root);
        store.Write(new PendingUpdate("0.4.0", installer, DateTimeOffset.UtcNow));

        var pending = new PendingUpdateStore(_root).Read();
        Assert.NotNull(pending);
        Assert.Equal("0.4.0", pending!.Version);
    }

    /// <summary>
    /// The installer lives in %TEMP%, which Windows and cleanup tools empty
    /// freely — a marker pointing at a vanished file must read as "nothing
    /// staged", not as something to hand to the installer.
    /// </summary>
    [Fact]
    public void IgnoresAMarkerWhoseInstallerIsGone()
    {
        var store = new PendingUpdateStore(_root);
        store.Write(new PendingUpdate(
            "0.4.0", Path.Combine(_root, "vanished.exe"), DateTimeOffset.UtcNow));

        Assert.Null(store.Read());
    }

    [Fact]
    public void ClearRemovesTheMarker()
    {
        var installer = Path.Combine(_root, "EQDeeps-Setup.exe");
        Directory.CreateDirectory(_root);
        File.WriteAllText(installer, "x");

        var store = new PendingUpdateStore(_root);
        store.Write(new PendingUpdate("0.4.0", installer, DateTimeOffset.UtcNow));
        store.Clear();

        Assert.Null(store.Read());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
