using EQDeeps.Core.Gear;

namespace EQDeeps.Server;

/// <summary>
/// Watches for a character's <c>/outputfile inventory</c> dump and records each
/// distinct version as a gear snapshot.
///
/// <para>The game overwrites one file in the install root, so "has it changed"
/// is a last-write-time question — polled, like the log tail, rather than
/// hooked with a FileSystemWatcher: one stat every few seconds is cheaper than
/// the duplicate-event handling a watcher would need, and the dump is a manual
/// act that nobody performs twice a second.</para>
///
/// <para>Nothing here ever asks the game to write the file. The player types
/// the command; this only notices.</para>
/// </summary>
public sealed class GearWatcher
{
    /// <summary>What the player has to type. Surfaced to the UI so the nudge can quote it exactly.</summary>
    public const string Command = "/outputfile inventory";

    private readonly string _character;
    private readonly string _server;
    private readonly GearStore _store;
    private readonly List<string> _candidates;

    private DateTime _lastWriteSeen = DateTime.MinValue;

    public GearWatcher(string character, string server, string logPath, GearStore store)
    {
        _character = character;
        _server = server;
        _store = store;
        _candidates = CandidatePaths(character, server, logPath);
    }

    /// <summary>
    /// Where we expect the dump. The first candidate even when nothing exists
    /// yet, so a player whose command appeared to do nothing can be shown the
    /// exact path being watched instead of being told "no gear found".
    /// </summary>
    public string ExpectedPath => _candidates.Count > 0 ? _candidates[0] : string.Empty;

    /// <summary>
    /// Reads the dump if it has changed since the last look. Returns true only
    /// when a genuinely new snapshot was stored. Never throws: a file being
    /// rewritten as we read it is an ordinary race, not an error worth
    /// surfacing — the next poll gets it.
    /// </summary>
    public bool Poll()
    {
        foreach (var path in _candidates)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    continue;
                }

                if (info.LastWriteTime <= _lastWriteSeen)
                {
                    return false;
                }

                var lines = File.ReadAllLines(path);
                _lastWriteSeen = info.LastWriteTime;

                var snapshot = InventoryFileParser.Parse(
                    lines, _character, _server, info.LastWriteTime);
                return snapshot is not null && _store.Record(snapshot);
            }
            catch (IOException)
            {
                // Being written right now, or gone. Try again next poll.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    /// <summary>
    /// Where the dump could be, best first. The session's own log path is the
    /// strongest evidence available — logs live in &lt;install&gt;\Logs, so the
    /// log we are already reading names the install root outright, with no
    /// guessing. Discovery is the fallback for logs opened from a copy that
    /// sits outside any install.
    /// </summary>
    private static List<string> CandidatePaths(string character, string server, string logPath)
    {
        var fileName = InventoryFileParser.FileNameFor(character, server);
        var roots = new List<string>();

        try
        {
            var logDir = Path.GetDirectoryName(Path.GetFullPath(logPath));
            if (logDir is not null &&
                Path.GetFileName(logDir).Equals("Logs", StringComparison.OrdinalIgnoreCase) &&
                Path.GetDirectoryName(logDir) is { } installRoot)
            {
                roots.Add(installRoot);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (IOException)
        {
        }

        try
        {
            roots.AddRange(LogDiscovery.InstallRoots().Select(r => r.Dir));
        }
        catch (Exception)
        {
            // Discovery is best-effort by contract; the log-derived root stands.
        }

        return roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(root => Path.Combine(root, fileName))
            .ToList();
    }
}
