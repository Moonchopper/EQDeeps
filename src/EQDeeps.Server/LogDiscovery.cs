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
///  2. The DGC-EverQuest* uninstall registry keys (custom install locations).
///  3. Conventional install paths: the Daybreak / legacy SOE public-folder
///     locations and the default Steam library.
/// Candidates are deduped (first source wins) and returned newest-written
/// first — the log the user actually plays on sorts to the top.
///
/// Nothing here assumes a single product name. "EverQuest Legends" installs
/// beside "EverQuest" under the same publisher folder, registers its own
/// uninstall key, and is routinely installed to a drive that isn't the one
/// %PUBLIC% points at — so the publisher directory is enumerated rather than
/// guessed, on every fixed drive.
/// </summary>
public static class LogDiscovery
{
    public static List<DiscoveredLog> Discover()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<DiscoveredLog>();
        foreach (var (installDir, source) in InstallRoots())
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
    /// Install directories to try, best source first, deduped. Logs are one
    /// thing that lives under an install root; the inventory dump written by
    /// <c>/outputfile inventory</c> is another, so this is shared rather than
    /// private to log scanning.
    /// </summary>
    public static List<(string Dir, string Source)> InstallRoots()
    {
        var candidates = new List<(string Dir, string Source)>();
        foreach (var dir in RunningGameDirectories())
        {
            candidates.Add((dir, "running EverQuest"));
        }

        foreach (var dir in RegistryInstallDirectories())
        {
            candidates.Add((dir, "registry"));
        }

        foreach (var dir in ConventionalInstallDirectories())
        {
            candidates.Add((dir, "known install path"));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<(string Dir, string Source)>();
        foreach (var candidate in candidates)
        {
            string full;
            try
            {
                full = Path.GetFullPath(candidate.Dir);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (seen.Add(full))
            {
                roots.Add((full, candidate.Source));
            }
        }

        return roots;
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
        foreach (var publicFolder in PublicFolders())
        {
            foreach (var publisher in new[] { "Daybreak Game Company", "Sony Online Entertainment" })
            {
                results.AddRange(ScanInstalledGames(
                    Path.Combine(publicFolder, publisher, "Installed Games")));
            }
        }

        var programsX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(programsX86))
        {
            results.AddRange(ScanInstalledGames(
                Path.Combine(programsX86, "Steam", "steamapps", "common")));
        }

        return results;
    }

    /// <summary>
    /// Public-profile roots to search. %PUBLIC% is the profile Windows created,
    /// but the launcher happily installs to D:\Users\Public\… on a machine whose
    /// profile lives on C:, so every fixed drive gets the same treatment.
    /// </summary>
    private static IEnumerable<string> PublicFolders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var publicFolder = Environment.GetEnvironmentVariable("PUBLIC");
        if (!string.IsNullOrEmpty(publicFolder) && seen.Add(publicFolder))
        {
            yield return publicFolder;
        }

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            if (drive.DriveType != DriveType.Fixed)
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.Combine(drive.RootDirectory.FullName, "Users", "Public");
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>
    /// EverQuest installs directly under a publisher's "Installed Games"
    /// directory (or a Steam library's "common"). Enumerating it instead of
    /// guessing product names is what lets "EverQuest Legends" — and whatever
    /// the next one is called — be found. Exposed for tests.
    /// </summary>
    public static List<string> ScanInstalledGames(string parentDir)
    {
        var results = new List<string>();
        if (!Directory.Exists(parentDir))
        {
            return results;
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(parentDir, "EverQuest*"))
            {
                if (File.Exists(Path.Combine(dir, "eqgame.exe")) ||
                    Directory.Exists(Path.Combine(dir, "Logs")))
                {
                    results.Add(dir);
                }
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

    /// <summary>
    /// Every DGC-EverQuest* uninstall entry, across both hives and both
    /// registry views. The key name carries the product ("DGC-EverQuest",
    /// "DGC-EverQuest Legends"), so it is matched by prefix — and a 32-bit
    /// installer writes its key under WOW6432Node, which the 64-bit view
    /// cannot see.
    /// </summary>
    private static IEnumerable<string> RegistryInstallDirectories()
    {
        var results = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            return results;
        }

        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(UninstallKey);
                    if (uninstall is null)
                    {
                        continue;
                    }

                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        if (!name.StartsWith("DGC-EverQuest", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        using var key = uninstall.OpenSubKey(name);
                        if (key is null)
                        {
                            continue;
                        }

                        // InstallLocation is already a directory; the other two
                        // are commands whose executable sits in one.
                        foreach (var valueName in new[] { "InstallLocation", "UninstallString", "DisplayIcon" })
                        {
                            if (key.GetValue(valueName) is string value &&
                                TryResolveDirectory(value, out var dir))
                            {
                                results.Add(dir);
                                break;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Registry access can fail under restricted accounts; not fatal.
                }
            }
        }

        return results;
    }

    private static bool TryResolveDirectory(string value, out string directory)
    {
        directory = string.Empty;
        var trimmed = value.Trim();

        // A quoted command carries its arguments outside the quotes
        // ("…\uninstall.exe" /S); an unquoted one usually has none.
        if (trimmed.StartsWith('"'))
        {
            var close = trimmed.IndexOf('"', 1);
            trimmed = close > 1 ? trimmed[1..close] : trimmed.Trim('"');
        }

        if (trimmed.Length == 0)
        {
            return false;
        }

        if (Directory.Exists(trimmed))
        {
            directory = trimmed;
            return true;
        }

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
