using NetSparkleUpdater;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.SignatureVerifiers;

namespace EQDeeps.Server.Updates;

/// <summary>Where the update flow currently stands, for the UI to render.</summary>
public enum UpdateStage
{
    Idle,
    Checking,
    Available,
    Downloading,
    Staged,
    Failed,
}

/// <summary>Everything /api/update/state hands the SPA. Immutable snapshot.</summary>
public sealed record UpdateState(
    string Version,
    UpdateStage Stage,
    UpdateMode Mode,
    string? LatestVersion = null,
    string? ReleaseNotes = null,
    string? ReleaseUrl = null,
    int DownloadPercent = 0,
    long DownloadedBytes = 0,
    long DownloadSizeBytes = 0,
    bool PromptRequired = false,
    bool RestartRequired = false,
    bool RequiresElevation = false,
    bool CanSelfInstall = true,
    DateTimeOffset? LastCheckedUtc = null,
    string? Error = null);

/// <summary>
/// The update loop (feature F22, ADR-010). Checks GitHub for a newer release,
/// asks the user how they feel about it according to their standing
/// preferences, downloads in the background, and hands the installer off at
/// exit so an update never interrupts a live parse.
///
/// NetSparkle does the fetching and the Ed25519 verification; the consent
/// policy is ours (<see cref="UpdatePreferences"/>) so it stays pure and
/// testable, and so the answers survive with no UI attached. Nothing here ever
/// installs on its own initiative: <see cref="UserInteractionMode.DownloadNoInstall"/>
/// means NetSparkle stages and stops.
///
/// Every failure is soft. An offline machine, a rate-limited API, a corrupt
/// download — all leave EQDeeps running exactly as it would have.
/// </summary>
public sealed class UpdateService : IDisposable
{
    /// <summary>
    /// Ed25519 public key for release verification. The matching private key
    /// never leaves the release workflow's secrets. Replacing this key is a
    /// breaking change for every installed copy — see docs/release-signing.md.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so a test can assert the placeholder never
    /// ships: with it still in place auto-update silently disables itself, and
    /// the only other signal is a log line nobody reads.
    /// </remarks>
    internal const string PublicKey = "YxA3OlqIw4vxDda3+dwhqbS419SOyxgglRXzADUyWbs=";

    private const string AppCastUrl =
        "https://github.com/Moonchopper/EQDeeps/releases/latest/download/appcast.xml";

    private const string ReleasesPage = "https://github.com/Moonchopper/EQDeeps/releases/latest";

    /// <summary>
    /// How often a long-running session re-checks. EQDeeps is commonly left
    /// open for days, so this loop is the only thing that tells such a session
    /// a release exists.
    ///
    /// Five minutes looks aggressive and isn't: installed builds fetch a ~2 KB
    /// app cast from a release asset, which is CDN-served and carries no API
    /// rate limit, so this is ~24 KB an hour. Frequent checking also cannot
    /// turn into nagging — a declined release is remembered for the run, and
    /// auto mode stages a given release once — so the only thing that changes
    /// is how quickly a new release is noticed.
    ///
    /// The one shape with a ceiling is the portable/source fallback, which uses
    /// the GitHub REST API (60 requests/hour per IP unauthenticated). At this
    /// interval that is 12/hour per instance; several portable copies on one
    /// machine would start to eat into it, and a failed check is silent anyway.
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly UpdatePreferenceStore _preferences;
    private readonly PendingUpdateStore _pending;
    private readonly UpdateInstaller _installer;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<UpdateService> _log;
    private readonly SparkleUpdater? _sparkle;
    private readonly HashSet<string> _declinedThisRun = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _stopping = new();

    private UpdateState _state;
    private AppCastItem? _offered;

    /// <summary>Set when the user chose "update now" rather than "on exit".</summary>
    private volatile bool _applyWhenReady;

    public UpdateService(
        UpdatePreferenceStore preferences,
        PendingUpdateStore pending,
        UpdateInstaller installer,
        IHostApplicationLifetime lifetime,
        ILogger<UpdateService> log)
    {
        _preferences = preferences;
        _pending = pending;
        _installer = installer;
        _lifetime = lifetime;
        _log = log;

        var mode = preferences.Read().Mode;
        _state = new UpdateState(AppVersion.Current, UpdateStage.Idle, mode)
        {
            CanSelfInstall = IsInstalledBuild && HasReleaseKey,
            ReleaseUrl = ReleasesPage,
        };

        // A portable or source build has no uninstaller to hand off to, so it
        // never stages anything — it only ever says "a newer version exists".
        // Same fallback if the release key was never substituted in: without it
        // nothing could be verified, and staging an unverifiable installer is
        // the one outcome worse than not updating.
        if (!IsInstalledBuild || !HasReleaseKey)
        {
            if (IsInstalledBuild)
            {
                log.LogWarning(
                    "No Ed25519 release key compiled in; updates are notify-only. See docs/release-signing.md.");
            }

            _sparkle = null;
            return;
        }

        _sparkle = new SparkleUpdater(AppCastUrl, new Ed25519Checker(SecurityMode.Strict, PublicKey, publicKeyFile: null))
        {
            UIFactory = null, // the SPA is the UI; NetSparkle must never draw
            UserInteractionMode = UserInteractionMode.DownloadNoInstall,
            RelaunchAfterUpdate = false, // we own the handoff (UpdateInstaller)
            CheckServerFileName = false, // GitHub asset URLs redirect; trust ours
        };
        _sparkle.DownloadMadeProgress += OnDownloadProgress;
        _sparkle.DownloadFinished += OnDownloadFinished;
        _sparkle.DownloadHadError += OnDownloadError;
        _sparkle.DownloadedFileIsCorrupt += OnDownloadCorrupt;
    }

    /// <summary>
    /// True when this copy was installed by the Inno Setup package — the only
    /// shape that can be updated in place, since only it has an uninstaller and
    /// a stable install directory.
    /// </summary>
    private static bool IsInstalledBuild =>
        File.Exists(Path.Combine(UpdateInstaller.InstallDirectory, "unins000.exe"));

    /// <summary>False in dev trees, where the placeholder key is still in place.</summary>
    private static bool HasReleaseKey => !PublicKey.StartsWith("REPLACE_WITH", StringComparison.Ordinal);

    public UpdateState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    /// <summary>Background loop: check shortly after launch, then periodically.</summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Let the app finish opening a log first; the update check is
                // never the most interesting thing happening at startup.
                await Task.Delay(TimeSpan.FromSeconds(5), _stopping.Token).ConfigureAwait(false);
                while (!_stopping.IsCancellationRequested)
                {
                    await CheckAsync(userInitiated: false, _stopping.Token).ConfigureAwait(false);
                    await Task.Delay(CheckInterval, _stopping.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>
    /// Fetch the appcast and act on the user's standing answers.
    /// <paramref name="userInitiated"/> comes from the "check for updates"
    /// button and overrides every prior "don't tell me".
    /// </summary>
    public async Task CheckAsync(bool userInitiated, CancellationToken cancellationToken = default)
    {
        if (!await _checkGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return; // a check is already running; the UI polls for its result
        }

        try
        {
            var preferences = _preferences.Read();
            if (preferences.Mode == UpdateMode.Manual && !userInitiated)
            {
                return;
            }

            Mutate(s => s with { Stage = UpdateStage.Checking, Error = null });

            var latest = _sparkle is null
                ? await CheckWithoutInstallingAsync(cancellationToken).ConfigureAwait(false)
                : await CheckViaAppCastAsync().ConfigureAwait(false);

            var action = preferences.Decide(latest, AppVersion.Current, _declinedThisRun, userInitiated);
            if (action == UpdateAction.None)
            {
                Mutate(s => s with
                {
                    Stage = s.RestartRequired ? UpdateStage.Staged : UpdateStage.Idle,
                    LatestVersion = latest,
                    PromptRequired = false,
                    LastCheckedUtc = DateTimeOffset.UtcNow,
                });
                return;
            }

            Mutate(s => s with
            {
                // An already-staged update stays staged: a check that merely
                // re-confirms the same release must not lose the fact that its
                // installer is downloaded and waiting.
                Stage = s.RestartRequired ? UpdateStage.Staged : UpdateStage.Available,
                LatestVersion = latest,
                ReleaseNotes = _offered?.Description,
                ReleaseUrl = ReleasesPage,
                PromptRequired = action == UpdateAction.Prompt,
                LastCheckedUtc = DateTimeOffset.UtcNow,
            });

            if (action == UpdateAction.Stage)
            {
                await StageAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Offline, DNS down, GitHub 503, malformed appcast: all non-events.
            _log.LogDebug(ex, "Update check failed");
            Mutate(s => s with { Stage = UpdateStage.Idle, Error = null });
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task<string?> CheckViaAppCastAsync()
    {
        var info = await _sparkle!.CheckForUpdatesQuietly(ignoreSkippedVersions: true).ConfigureAwait(false);
        if (info.Status != UpdateStatus.UpdateAvailable || info.Updates.Count == 0)
        {
            _offered = null;
            return null;
        }

        // The appcast is newest-first; trust our own comparison rather than the
        // feed's ordering so a mis-sorted feed can't push a downgrade.
        _offered = info.Updates
            .Where(u => u.Version is not null && AppVersion.IsNewer(u.Version, AppVersion.Current))
            .OrderByDescending(u => AppVersion.TryParse(u.Version!, out var v) ? v : new Version(0, 0))
            .FirstOrDefault();

        return _offered?.Version;
    }

    /// <summary>
    /// Portable and source builds can't install anything, but they can still
    /// tell the user a release exists — the behaviour EQDeeps shipped before
    /// auto-update, kept alive for the shapes that need it.
    /// </summary>
    private static async Task<string?> CheckWithoutInstallingAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EQDeeps/" + AppVersion.Current);
        var json = await http
            .GetStringAsync("https://api.github.com/repos/Moonchopper/EQDeeps/releases/latest", cancellationToken)
            .ConfigureAwait(false);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
    }

    /// <summary>
    /// The user said yes (or auto mode never asked): download and stage.
    /// </summary>
    /// <param name="applyWhenReady">
    /// True for "update now" — install and relaunch the moment the download
    /// lands, instead of waiting for the user to close the app.
    /// </param>
    public async Task StageAsync(bool applyWhenReady = false)
    {
        // Already downloaded and waiting: "update now" applies what is on disk
        // rather than fetching 60 MB again. Checked before anything that needs
        // the app cast, so this still works with no network — which is rather
        // the point of having staged it in advance.
        if (applyWhenReady && _state.Stage != UpdateStage.Downloading && _pending.Read() is not null)
        {
            ApplyNow(out _);
            return;
        }

        if (_sparkle is null || _offered is null)
        {
            return;
        }

        // Accepting with "always update" set turns on auto mode first, which
        // stages by itself, and then the UI asks to stage again. NetSparkle
        // ignores the second download, but without this guard the progress we
        // report would snap back to 0% mid-download.
        if (_state.Stage == UpdateStage.Downloading)
        {
            // A second call can still upgrade "on exit" to "now" — the user may
            // have changed their mind while the bytes were coming down.
            _applyWhenReady |= applyWhenReady;
            return;
        }

        _applyWhenReady = applyWhenReady;

        // Size comes from the app cast so the UI can show "12.4 / 57.8 MB"
        // from the very first frame, before any progress event has fired.
        Mutate(s => s with
        {
            Stage = UpdateStage.Downloading,
            DownloadPercent = 0,
            DownloadedBytes = 0,
            DownloadSizeBytes = _offered.UpdateSize,
            PromptRequired = false,
        });
        await _sparkle.InitAndBeginDownload(_offered).ConfigureAwait(false);
    }

    // ---- consent levers ---------------------------------------------------

    /// <summary>"Not right now" — forgotten on restart, or on a manual check.</summary>
    public void DeclineForThisRun()
    {
        if (_state.LatestVersion is { } version)
        {
            _declinedThisRun.Add(AppVersion.Normalize(version));
        }

        Mutate(s => s with { PromptRequired = false, Stage = UpdateStage.Idle });
    }

    /// <summary>"Skip this release" — silent until something newer ships.</summary>
    public void SkipOfferedRelease()
    {
        if (_state.LatestVersion is { } version)
        {
            _preferences.Update(p => p.Skip(version));
        }

        Mutate(s => s with { PromptRequired = false, Stage = UpdateStage.Idle });
    }

    /// <summary>"Don't ask again on this build" — silent until they update.</summary>
    public void MuteForCurrentVersion()
    {
        _preferences.Update(p => p.MuteOn(AppVersion.Current));
        Mutate(s => s with { PromptRequired = false, Stage = UpdateStage.Idle });
    }

    public void SetMode(UpdateMode mode)
    {
        var updated = _preferences.Update(p => p with { Mode = mode });
        Mutate(s => s with { Mode = updated.Mode });
        if (mode == UpdateMode.Auto && _state.Stage == UpdateStage.Available)
        {
            _ = StageAsync();
        }
    }

    // ---- applying ---------------------------------------------------------

    /// <summary>
    /// Apply immediately and come back up. Used by the "Restart now" button,
    /// the only path allowed to raise a UAC prompt, because the user just asked.
    /// </summary>
    public bool ApplyNow(out string error)
    {
        error = string.Empty;
        if (_pending.Read() is not { } pending)
        {
            return false;
        }

        if (!_installer.TryApply(pending, relaunch: true, out error))
        {
            var reason = error;
            _log.LogWarning("Refusing to run staged installer: {Reason}", reason);
            _pending.Clear();
            Mutate(s => s with { Stage = UpdateStage.Failed, RestartRequired = false, Error = reason });
            return false;
        }

        _pending.Clear();

        // The handoff script is sitting in a loop waiting for this process to
        // exit before it can replace our files — so we have to actually go.
        // Without this the installer never runs, the script times out after two
        // minutes, and "restart to update" looks like it did nothing at all.
        _log.LogInformation("Applying update v{Version} now; shutting down", pending.Version);
        _lifetime.StopApplication();
        return true;
    }

    /// <summary>
    /// Called on the way out: hand the staged installer to the batch script so
    /// the next launch is already the new version. Skipped when it would need
    /// elevation, since a UAC prompt after the window closed looks like malware.
    /// </summary>
    public void ApplyOnExit()
    {
        if (_pending.Read() is not { } pending || UpdateInstaller.RequiresElevation())
        {
            return;
        }

        if (_installer.TryApply(pending, relaunch: false, out var error))
        {
            _pending.Clear();
            _log.LogInformation("Staged update v{Version} will install after exit", pending.Version);
        }
        else
        {
            _log.LogWarning("Refusing to run staged installer: {Reason}", error);
            _pending.Clear();
        }
    }

    // ---- NetSparkle callbacks --------------------------------------------

    private void OnDownloadProgress(object sender, AppCastItem item, NetSparkleUpdater.Events.ItemDownloadProgressEventArgs args) =>
        Mutate(s => s with
        {
            DownloadPercent = args.ProgressPercentage,
            DownloadedBytes = args.BytesReceived,
            // Prefer what the transfer reports; the app cast figure is only a
            // starting estimate and a chunked response may not set this at all.
            DownloadSizeBytes = args.TotalBytesToReceive > 0
                ? args.TotalBytesToReceive
                : s.DownloadSizeBytes,
        });

    private void OnDownloadFinished(AppCastItem item, string path)
    {
        _pending.Write(new PendingUpdate(item.Version ?? "unknown", path, DateTimeOffset.UtcNow));
        Mutate(s => s with
        {
            Stage = UpdateStage.Staged,
            DownloadPercent = 100,
            DownloadedBytes = s.DownloadSizeBytes,
            RestartRequired = true,
            RequiresElevation = UpdateInstaller.RequiresElevation(),
        });
        _log.LogInformation("Update v{Version} staged at {Path}", item.Version, path);

        if (_applyWhenReady)
        {
            _applyWhenReady = false;
            ApplyNow(out _);
        }
    }

    private void OnDownloadError(AppCastItem item, string? path, Exception exception)
    {
        _log.LogDebug(exception, "Update download failed");
        Mutate(s => s with { Stage = UpdateStage.Failed, Error = "The download didn't finish." });
    }

    private void OnDownloadCorrupt(AppCastItem item, string path)
    {
        // Signature mismatch: the file is not what we published. Say so plainly
        // rather than silently retrying a bad or tampered download.
        _log.LogWarning("Discarding update v{Version}: signature verification failed", item.Version);
        _pending.Clear();
        Mutate(s => s with
        {
            Stage = UpdateStage.Failed,
            Error = "The download failed its signature check and was discarded.",
        });
    }

    private void Mutate(Func<UpdateState, UpdateState> change)
    {
        lock (_stateGate)
        {
            _state = change(_state);
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        _checkGate.Dispose();
        _sparkle?.Dispose();
    }
}
