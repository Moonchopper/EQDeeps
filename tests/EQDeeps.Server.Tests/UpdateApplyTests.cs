using EQDeeps.Server.Updates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// Applying a staged update is a two-part handshake and both halves are easy to
/// get wrong silently. The installer runs in a detached script that waits for
/// this process to exit before it can replace our files — so if EQDeeps does
/// not actually shut down, the script waits two minutes and gives up, and
/// "restart to update" appears to do nothing at all. That shipped once; these
/// tests exist so it cannot ship again.
/// </summary>
public class UpdateApplyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eqdeeps-apply-" + Guid.NewGuid().ToString("N"));

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public bool StopRequested { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => StopRequested = true;
    }

    /// <summary>Records the hand-off instead of spawning a real installer.</summary>
    private sealed class StubInstaller(bool succeeds) : UpdateInstaller
    {
        public int Applied { get; private set; }

        public bool? Relaunched { get; private set; }

        public override bool TryApply(PendingUpdate pending, bool relaunch, out string error)
        {
            Applied++;
            Relaunched = relaunch;
            error = succeeds ? string.Empty : "signature check failed";
            return succeeds;
        }
    }

    private (UpdateService Service, StubInstaller Installer, StubLifetime Lifetime) Build(
        bool installerSucceeds = true)
    {
        var installer = new StubInstaller(installerSucceeds);
        var lifetime = new StubLifetime();
        var service = new UpdateService(
            new UpdatePreferenceStore(_root),
            new PendingUpdateStore(_root),
            installer,
            lifetime,
            NullLogger<UpdateService>.Instance);
        return (service, installer, lifetime);
    }

    private PendingUpdate StageOnDisk()
    {
        Directory.CreateDirectory(_root);
        var installerPath = Path.Combine(_root, "EQDeeps-Setup-9.9.9.exe");
        File.WriteAllText(installerPath, "stand-in for a signed installer");
        var pending = new PendingUpdate("9.9.9", installerPath, DateTimeOffset.UtcNow);
        new PendingUpdateStore(_root).Write(pending);
        return pending;
    }

    [Fact]
    public void ApplyingShutsTheAppDownSoTheInstallerCanRun()
    {
        StageOnDisk();
        var (service, installer, lifetime) = Build();

        Assert.True(service.ApplyNow(out _));

        Assert.Equal(1, installer.Applied);
        Assert.True(installer.Relaunched); // the user expects to come back up
        Assert.True(
            lifetime.StopRequested,
            "ApplyNow handed off to the installer but never exited — the script would time out.");
    }

    [Fact]
    public void ApplyingClearsTheStagedMarkerSoItIsNotRunTwice()
    {
        StageOnDisk();
        var (service, _, _) = Build();

        service.ApplyNow(out _);

        Assert.Null(new PendingUpdateStore(_root).Read());
    }

    [Fact]
    public void ApplyingDoesNothingWhenNothingIsStaged()
    {
        var (service, installer, lifetime) = Build();

        Assert.False(service.ApplyNow(out _));

        Assert.Equal(0, installer.Applied);
        Assert.False(lifetime.StopRequested);
    }

    /// <summary>
    /// A rejected installer must not take the app down with it: the user keeps
    /// working on the version they have, and is told why.
    /// </summary>
    [Fact]
    public void ARejectedInstallerDoesNotShutTheAppDown()
    {
        StageOnDisk();
        var (service, _, lifetime) = Build(installerSucceeds: false);

        Assert.False(service.ApplyNow(out var error));

        Assert.False(lifetime.StopRequested);
        Assert.Equal("signature check failed", error);
        Assert.Equal(UpdateStage.Failed, service.State.Stage);
        Assert.False(service.State.RestartRequired);
        // The bad installer is discarded rather than left to be retried forever.
        Assert.Null(new PendingUpdateStore(_root).Read());
    }

    /// <summary>
    /// "Update now" on an already-downloaded release should install what is on
    /// disk, not spend another 60 MB fetching the same file.
    /// </summary>
    [Fact]
    public async Task UpdateNowOnAnAlreadyStagedReleaseAppliesWithoutRedownloading()
    {
        StageOnDisk();
        var (service, installer, lifetime) = Build();

        await service.StageAsync(applyWhenReady: true);

        Assert.Equal(1, installer.Applied);
        Assert.True(lifetime.StopRequested);
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
