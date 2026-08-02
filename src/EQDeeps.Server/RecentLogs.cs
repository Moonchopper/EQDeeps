using System.Text.Json;

namespace EQDeeps.Server;

/// <summary>
/// Most-recently-opened log paths, persisted (one small JSON beside the
/// DocumentStore documents) so the empty state and the detected-logs picker
/// can offer previously tracked logs back even when the game isn't running
/// and discovery finds nothing — EMU logs, copies, files opened by hand.
/// Capped MRU; missing files are filtered when served, so deleted logs age
/// out naturally. Writes are atomic (temp + move), matching DocumentStore.
/// </summary>
public sealed class RecentLogs
{
    private const int Cap = 10;
    private readonly string _path;
    private readonly object _gate = new();

    public RecentLogs(string? root = null)
    {
        _path = Path.Combine(
            root ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQDeeps"),
            "recent-logs.json");
    }

    /// <summary>Record a successful open: the path moves to the front of the list.</summary>
    public void Touch(string path)
    {
        lock (_gate)
        {
            var list = ReadLocked();
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, path);
            if (list.Count > Cap)
            {
                list.RemoveRange(Cap, list.Count - Cap);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list));
            File.Move(temp, _path, overwrite: true);
        }
    }

    /// <summary>Most recent first. Paths only — existence is the caller's concern.</summary>
    public List<string> List()
    {
        lock (_gate)
        {
            return ReadLocked();
        }
    }

    private List<string> ReadLocked()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (JsonException)
        {
            return []; // corrupt: start fresh rather than fail forever
        }
        catch (IOException)
        {
            return [];
        }
    }
}
