using System.Text.Json;
using EQDeeps.Core.Gear;

namespace EQDeeps.Server;

/// <summary>
/// The gear snapshots recorded for each character, one small JSON per character
/// beside the DocumentStore documents. Unlike parsed log records — which are
/// rebuilt from the log on every open — these cannot be recomputed: the
/// inventory dump is overwritten by the next one, so a snapshot not kept here
/// is gone. That is the whole reason this store exists.
///
/// <para>Writes are atomic (temp + move) and a corrupt file starts fresh,
/// matching <see cref="RecentLogs"/> and <see cref="DocumentStore"/>.</para>
/// </summary>
public sealed class GearStore
{
    /// <summary>
    /// Enough history for a long-running character without letting the file
    /// grow unbounded. Oldest snapshots fall off first; the newest is the one
    /// that matters for live play.
    /// </summary>
    private const int Cap = 200;

    private readonly string _root;
    private readonly object _gate = new();

    public GearStore(string? root = null)
    {
        _root = Path.Combine(
            root ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQDeeps"),
            "gear");
    }

    /// <summary>Snapshots for one character, oldest first.</summary>
    public List<GearSnapshot> List(string character, string server)
    {
        lock (_gate)
        {
            var path = PathFor(character, server);
            var list = ReadLocked(path);
            if (Repair(list))
            {
                WriteLocked(path, list);
            }

            return list;
        }
    }

    /// <summary>
    /// Drops anything from a stored snapshot's equipped set that today's rule
    /// says is a container, and re-keys the snapshot if so. Returns whether
    /// anything changed.
    ///
    /// <para>An early build recorded a personal depot's contents as worn gear.
    /// Snapshots cannot be recomputed — the dump they came from was overwritten
    /// by the next one — so the choice was to repair them in place or leave a
    /// player's history permanently claiming they fought in twelve tradeskill
    /// components. Repairing removes only rows the parser would never accept
    /// now; nothing that is genuinely gear can match.</para>
    /// </summary>
    private static bool Repair(List<GearSnapshot> list)
    {
        var changed = false;
        for (var i = 0; i < list.Count; i++)
        {
            var kept = list[i].Equipped
                .Where(item => InventoryFileParser.IsEquipmentSlot(item.Location))
                .ToList();
            if (kept.Count == list[i].Equipped.Count)
            {
                continue;
            }

            list[i] = list[i] with { Equipped = kept, Hash = InventoryFileParser.HashOf(kept) };
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Records a snapshot unless it is indistinguishable from the newest one
    /// already held. Returns whether anything was added — the caller uses that
    /// to decide whether the UI has news.
    ///
    /// <para>Re-running <c>/outputfile inventory</c> is a normal, frequent act;
    /// it should cost nothing when the gear has not moved.</para>
    /// </summary>
    public bool Record(GearSnapshot snapshot)
    {
        lock (_gate)
        {
            var path = PathFor(snapshot.Character, snapshot.Server);
            var list = ReadLocked(path);
            if (list.Count > 0 && list[^1].Hash == snapshot.Hash)
            {
                return false;
            }

            list.Add(snapshot);
            list.Sort((a, b) => a.CapturedAt.CompareTo(b.CapturedAt));
            if (list.Count > Cap)
            {
                list.RemoveRange(0, list.Count - Cap);
            }

            WriteLocked(path, list);
            return true;
        }
    }

    private string PathFor(string character, string server) =>
        Path.Combine(_root, $"{Sanitize(character)}_{Sanitize(server)}.json");

    /// <summary>
    /// Character and server come from a filename the game wrote, so they are
    /// already tame — but they reach us as request-shaped strings, and a store
    /// that builds paths from them should not be the thing that trusts them.
    /// </summary>
    private static string Sanitize(string value)
    {
        var cleaned = new string(value
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .ToArray());
        return cleaned.Length > 0 ? cleaned : "unknown";
    }

    private void WriteLocked(string path, List<GearSnapshot> list)
    {
        Directory.CreateDirectory(_root);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list));
        File.Move(temp, path, overwrite: true);
    }

    private static List<GearSnapshot> ReadLocked(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<GearSnapshot>>(File.ReadAllText(path)) ?? []
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
