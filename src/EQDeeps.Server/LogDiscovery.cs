using System.Diagnostics;
using EQDeeps.Core.Sessions;
using Microsoft.Win32;

namespace EQDeeps.Server;

public sealed record DiscoveredLog(
    string Path,
    string Character,
    string Server,
    DateTime LastWriteTime,
    long SizeBytes,
    string Source);

/// <summary>
/// Finds EverQuest log files without the user hunting for paths. Sources, in
/// priority order:
///  1. A running EverQuest process (eqgame) — the game's executable directory
///     gives the install root, and logs live in &lt;root&gt;\Logs.
///  2. The DGC-EverQuest uninstall registry key (custom install locations).
///  3. Conventional install paths: the Daybreak / legacy SOE public-folder
///     locations and the default Steam library.
/// Candidates are deduped (first source wins) and returned newest-written
/// first — the log the user actually plays on sorts to the top.
/// </summary>
public static class LogDiscovery
{
    public static List<DiscoveredLog> Discover()
    {
        var candidates = new List<(string InstallDir, string Source)>();
        foreach (var dir in RunningGameDirectories())
        {
            candidates.Add((dir, "running EverQuest"));
        }

        if (TryGetInstallDirFromRegistry(out var registryDir))
        {
            candidates.Add((registryDir, "registry"));
        }

        foreach (var dir in ConventionalInstallDirectories())
        {
            candidates.Add((dir, "known install path"));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<DiscoveredLog>();
        foreach (var (installDir, source) in candidates)
        {
            var logsDir = Path.Combine(installDir, "Logs");
            if (!seen.Add(Path.GetFullPath(logsDir)))
            {
                continue;
            }

            results.AddRange(ScanLogsDirectory(logsDir, source));
        }

        return results.OrderByDescending(r => r.LastWriteTime).ToList();
    }

    /// <summary>
    /// Describes a single log file (recent-logs entries): parses
    /// character/server from the name when it matches the convention, falls
    /// back to the bare filename otherwise (EMU logs, renamed copies), and
    /// returns null for files that no longer exist.
    /// </summary>
    public static DiscoveredLog? Describe(string path, string source)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            if (!LogFileNames.TryParse(path, out var character, out var server))
            {
                character = Path.GetFileNameWithoutExtension(path);
                server = "unknown";
            }

            return new DiscoveredLog(
                info.FullName, character, server, info.LastWriteTime, info.Length, source);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Scans one Logs directory for character log files (exposed for tests).</summary>
    public static List<DiscoveredLog> ScanLogsDirectory(string logsDir, string source)
    {
        var results = new List<DiscoveredLog>();
        if (!Directory.Exists(logsDir))
        {
            return results;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(logsDir, "eqlog_*.txt"))
            {
                if (!LogFileNames.TryParse(file, out var character, out var server))
                {
                    continue;
                }

                var info = new FileInfo(file);
                results.Add(new DiscoveredLog(
                    info.FullName, character, server, info.LastWriteTime, info.Length, source));
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return results;
    }

    private static IEnumerable<string> RunningGameDirectories()
    {
        var directories = new List<string>();
        foreach (var process in Process.GetProcessesByName("eqgame"))
        {
            try
            {
                var exePath = process.MainModule?.FileName;
                if (exePath is not null && Path.GetDirectoryName(exePath) is { } dir)
                {
                    directories.Add(dir);
                }
            }
            catch (Exception)
            {
                // Access denied / process exited — a candidate, not a requirement.
            }
            finally
            {
                process.Dispose();
            }
        }

        return directories;
    }

    private static IEnumerable<string> ConventionalInstallDirectories()
    {
        var results = new List<string>();
        var publicFolder = Environment.GetEnvironmentVariable("PUBLIC");
        if (!string.IsNullOrEmpty(publicFolder))
        {
            results.Add(Path.Combine(publicFolder, "Daybreak Game Company", "Installed Games", "EverQuest"));
            results.Add(Path.Combine(publicFolder, "Sony Online Entertainment", "Installed Games", "EverQuest"));
        }

        var programsX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(programsX86))
        {
            results.Add(Path.Combine(programsX86, "Steam", "steamapps", "common", "EverQuest F2P"));
            results.Add(Path.Combine(programsX86, "Steam", "steamapps", "common", "EverQuest"));
        }

        return results.Where(Directory.Exists);
    }

    private static bool TryGetInstallDirFromRegistry(out string installDir)
    {
        installDir = string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DGC-EverQuest";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(UninstallKey);
                if (key is null)
                {
                    continue;
                }

                foreach (var valueName in new[] { "UninstallString", "DisplayIcon" })
                {
                    if (key.GetValue(valueName) is string command &&
                        TryExtractDirectory(command, out installDir))
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // Registry access can fail under restricted accounts; not fatal.
            }
        }

        return false;
    }

    private static bool TryExtractDirectory(string command, out string directory)
    {
        directory = string.Empty;
        var trimmed = command.Trim().Trim('"');
        var lastSlash = trimmed.LastIndexOf('\\');
        if (lastSlash <= 0)
        {
            return false;
        }

        var candidate = trimmed[..lastSlash];
        if (!Directory.Exists(candidate))
        {
            return false;
        }

        directory = candidate;
        return true;
    }
}
