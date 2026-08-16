using System.Security.Cryptography;
using System.Text;
using EQDeeps.Core.Cache;

namespace EQDeeps.Server;

/// <summary>
/// Where the parsed-record caches live and how a log finds its own (issue
/// #59, ADR-018): one <c>.eqdc</c> file per log path <i>per parser build</i>
/// under <c>%AppData%\EQDeeps\cache\</c>, named
/// <c>&lt;hash of full path&gt;-&lt;build&gt;.eqdc</c>. The path hash keeps a
/// character with the same name on two servers, or the same log under two
/// drive letters, from sharing one. The build suffix keeps a development
/// build and the installed release from taking turns wiping each other's:
/// a cache is only ever readable by the build that wrote it (the header is
/// stamped with the Core module version id), so with one file per log the two
/// would invalidate and rewrite it on every alternation, and every open on
/// the shared machine would be cold. With one file per build each stays warm.
///
/// <para>Recomputable, like the mob indexes: a cache is a faster way back to
/// state the log itself still holds. So an unopenable file starts fresh, a
/// log the cache no longer matches gets a new one, and nothing about opening
/// a session is allowed to fail because of the cache — <see cref="Open"/>
/// returns null and the session simply parses. The one hard rule is that two
/// sessions on one log do not both write: the second opener finds the file
/// held and goes without.</para>
///
/// <para>Swept on start-up: caches for logs that no longer exist, caches
/// nothing has touched in two months, and — since every rebuild of Core is a
/// new build with a new file — for each log, every other build's cache but
/// the most recently written one. Users delete characters and archive logs;
/// developers rebuild twenty times a day; a cache of a file that is gone, or
/// of a parser that is gone, is a few hundred megabytes for nothing. Keeping
/// the newest foreign build is what lets a dev build and the release
/// coexist.</para>
/// </summary>
public sealed class LogCacheStore
{
    /// <summary>A cache untouched this long is assumed to be for a log the user has moved on from.</summary>
    private static readonly TimeSpan SweepAge = TimeSpan.FromDays(60);

    public LogCacheStore(string? root = null)
    {
        Root = Path.Combine(
            root ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQDeeps"),
            "cache");
    }

    public string Root { get; }

    /// <summary>The cache file a log maps to for this build, whether or not it exists yet.</summary>
    public string PathFor(string logPath) => PathFor(logPath, LogCache.CoreVersion);

    /// <summary>The cache file a log maps to for the given parser build.</summary>
    public string PathFor(string logPath, Guid build) =>
        Path.Combine(Root, LogKey(logPath) + "-" + BuildKey(build) + ".eqdc");

    /// <summary>
    /// The path half of a cache file name. Full path, case-folded: Windows
    /// paths are case-insensitive, and the same log reached two ways is the
    /// same log.
    /// </summary>
    private static string LogKey(string logPath)
    {
        var key = Path.GetFullPath(logPath).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
    }

    private static string BuildKey(Guid build) => Convert.ToHexString(build.ToByteArray().AsSpan(0, 8));

    /// <summary>
    /// The cache for <paramref name="logPath"/>, validated against the log as
    /// it stands and ready to be restored from and appended to — or null when
    /// there can be no cache: a gzip archive (no resumable offsets), a file
    /// another session already holds, or any other trouble, none of which is
    /// worth failing the open for.
    /// </summary>
    public LogCache? Open(string logPath, bool emuMode)
    {
        if (logPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return LogCache.Open(PathFor(logPath), Path.GetFullPath(logPath), emuMode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes caches whose logs are gone, caches nothing has written in
    /// <see cref="SweepAge"/>, and, per log, every foreign build's cache but
    /// the newest — and the same for the world graph's label caches. Reads
    /// each header to learn the log path; a file that is not a readable cache
    /// at all is deleted too. Never throws: a sweep that fails is a little
    /// disk not reclaimed.
    /// </summary>
    public int Sweep(DateTime? now = null, Guid? build = null)
    {
        var cutoff = (now ?? DateTime.UtcNow) - SweepAge;
        var mine = BuildKey(build ?? LogCache.CoreVersion);
        var removed = 0;
        try
        {
            if (!Directory.Exists(Root))
            {
                return 0;
            }

            // Foreign builds' files, grouped by log, newest first — the head
            // of each group survives, the rest go.
            var foreign = new Dictionary<string, List<(string File, DateTime Written)>>(StringComparer.Ordinal);

            foreach (var file in Directory.EnumerateFiles(Root, "*.eqdc"))
            {
                try
                {
                    var written = File.GetLastWriteTimeUtc(file);
                    var log = LogCache.PeekLogPath(file);
                    if (written < cutoff || log is null || !File.Exists(log))
                    {
                        File.Delete(file);
                        removed++;
                        continue;
                    }

                    var name = Path.GetFileNameWithoutExtension(file);
                    var dash = name.LastIndexOf('-');
                    if (dash < 0)
                    {
                        // A file from before builds had their own — one
                        // this build will never read.
                        File.Delete(file);
                        removed++;
                        continue;
                    }

                    if (!string.Equals(name[(dash + 1)..], mine, StringComparison.Ordinal))
                    {
                        if (!foreign.TryGetValue(name[..dash], out var list))
                        {
                            foreign[name[..dash]] = list = [];
                        }

                        list.Add((file, written));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Held by a live session, or otherwise not ours to touch
                    // right now.
                }
            }

            // The world graph's label caches follow the same per-build rule:
            // this build's stays, the newest other build's stays, the rest go.
            var labelFiles = Directory.EnumerateFiles(Root, "map-labels-*.json")
                .Where(f => !string.Equals(Path.GetFileName(f), MapLabelCache.FileNameFor(build ?? LogCache.CoreVersion), StringComparison.OrdinalIgnoreCase))
                .Select(f => (File: f, Written: File.GetLastWriteTimeUtc(f)))
                .ToList();
            if (labelFiles.Count > 0)
            {
                foreign["map-labels"] = labelFiles;
            }

            foreach (var list in foreign.Values)
            {
                foreach (var (file, _) in list.OrderByDescending(x => x.Written).Skip(1))
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return removed;
    }
}
