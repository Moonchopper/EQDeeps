using System.Diagnostics;

namespace EQDeeps.Server.Updates;

/// <summary>
/// Runs a staged installer once EQDeeps itself has exited.
///
/// The handoff has to be a separate process, because Inno Setup cannot replace
/// an exe that is still running and we <em>are</em> that exe. So we write a
/// throwaway batch script that polls for our PID to disappear, runs the
/// installer silently, optionally relaunches us, and deletes itself. This is
/// the same shape NetSparkle uses internally; we do it ourselves so the flags,
/// the logging, and the "apply without a network round-trip" behaviour stay
/// under our control (NetSparkle's own path needs the AppCastItem in hand,
/// which a fresh process resuming an interrupted update does not have).
/// </summary>
public sealed class UpdateInstaller
{
    private readonly Func<string, (bool Trusted, string Reason)> _verify;

    public UpdateInstaller(Func<string, (bool, string)>? verify = null)
    {
        _verify = verify ?? (path => (Authenticode.IsTrusted(path, out var reason), reason));
    }

    /// <summary>
    /// The folder EQDeeps runs from — where the installer will write, and hence
    /// what decides whether applying an update needs elevation.
    /// </summary>
    public static string InstallDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)
        ?? AppContext.BaseDirectory;

    /// <summary>
    /// True when the install folder is not writable by this user, so running the
    /// installer would raise a UAC prompt. We refuse to do that behind their back
    /// on exit — a consent dialog appearing after you closed the app reads as
    /// malware — and require an explicit click instead.
    /// </summary>
    public static bool RequiresElevation()
    {
        try
        {
            var probe = Path.Combine(InstallDirectory, $".eqdeeps-write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// Verifies and launches the staged installer. Returns false without side
    /// effects when the file cannot be trusted — the caller should discard the
    /// staged update and fall back to telling the user to download by hand.
    /// </summary>
    /// <param name="pending">The staged installer.</param>
    /// <param name="relaunch">Restart EQDeeps once the install finishes.</param>
    public bool TryApply(PendingUpdate pending, bool relaunch, out string error)
    {
        var (trusted, reason) = _verify(pending.InstallerPath);
        if (!trusted)
        {
            error = reason;
            return false;
        }

        try
        {
            var script = WriteHandoffScript(pending, relaunch);
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            });
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string WriteHandoffScript(PendingUpdate pending, bool relaunch)
    {
        var script = Path.Combine(Path.GetTempPath(), $"eqdeeps-update-{Guid.NewGuid():N}.cmd");
        var pid = Environment.ProcessId;
        var exe = Environment.ProcessPath ?? Path.Combine(InstallDirectory, "EQDeeps.Server.exe");
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQDeeps");
        var log = Path.Combine(logDir, "update-install.log");

        // /SILENT rather than /VERYSILENT: a progress bar is the only signal the
        // user gets that the disk activity after closing EQDeeps is intentional.
        // /NORESTART because a parser has no business rebooting anyone's machine.
        var arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART";

        var relaunchLine = relaunch ? $"start \"\" \"{exe}\"" : "rem no relaunch requested";

        var body = $"""
            @echo off
            setlocal
            rem EQDeeps staged update -> v{pending.Version}. Generated at {pending.StagedUtc:O}.
            rem Wait for EQDeeps (PID {pid}) to exit so Inno Setup can replace its files.
            set /a _tries=0
            :wait
            tasklist /fi "PID eq {pid}" 2>nul | find "{pid}" >nul
            if errorlevel 1 goto run
            set /a _tries+=1
            if %_tries% GTR 120 goto giveup
            timeout /t 1 /nobreak >nul
            goto wait

            :run
            "{pending.InstallerPath}" {arguments} /LOG="{log}"
            {relaunchLine}
            del /f /q "{pending.InstallerPath}" >nul 2>&1
            goto done

            :giveup
            rem EQDeeps outlived the wait window: leave the installer staged so
            rem the next launch can apply it instead of downloading again.

            :done
            del /f /q "%~f0" >nul 2>&1
            """;

        File.WriteAllText(script, body);
        return script;
    }
}
