using System.Text.Json;
using EQDeeps.Core.Mobs;

namespace EQDeeps.Server;

/// <summary>
/// What each server's mobs do to the people standing in front of them, one
/// small JSON per server beside the learned health (F26).
///
/// <para>Per server for the same reason <see cref="MobHealthStore"/> is: the
/// evidence is about the world, and the estimate for a mob camped last week
/// should be on screen before tonight's first swing lands. Unlike health,
/// though, the key carries the defender's level — how hard something hits is a
/// fact about a pairing, not about the mob — so two characters of different
/// levels on one account contribute to different rows rather than to one
/// average describing neither. See <see cref="MobAttackIndex"/>.</para>
///
/// <para>This is a cache, not a system of record: every swing in it came from a
/// log file that still exists. A corrupt file therefore starts fresh without
/// ceremony and a failed write is swallowed — the cost is re-reading logs the
/// user still has, and it must never be the user's fight list.</para>
///
/// <para>Writes are atomic (temp + move), matching <see cref="MobHealthStore"/>,
/// <see cref="RecentLogs"/> and <see cref="DocumentStore"/>.</para>
/// </summary>
public sealed class MobAttackStore
{
    private readonly string _root;
    private readonly object _gate = new();

    /// <summary>Indexes in memory, keyed by sanitized server name.</summary>
    private readonly Dictionary<string, MobAttackIndex> _indexes = new(StringComparer.Ordinal);

    public MobAttackStore(string? root = null)
    {
        _root = Path.Combine(
            root ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQDeeps"),
            "attacks");
    }

    /// <summary>
    /// Folds a session's fights into the server's index and persists if any
    /// were new. Replaying a log re-offers every fight in it, so the merge is
    /// idempotent by fight start and a no-op costs one pass and no disk write.
    /// </summary>
    /// <returns>How many (fight, defender) samples had not been counted before.</returns>
    public int Record(string server, IEnumerable<AttackSample> samples)
    {
        lock (_gate)
        {
            var index = LoadLocked(server);
            var added = index.Add(samples);
            if (added > 0)
            {
                WriteLocked(server, index);
            }

            return added;
        }
    }

    /// <summary>Every matchup known on this server, best-evidenced first.</summary>
    public List<MobAttackEstimate> Estimates(string server)
    {
        lock (_gate)
        {
            return LoadLocked(server).Estimates();
        }
    }

    private MobAttackIndex LoadLocked(string server)
    {
        var key = Sanitize(server);
        if (_indexes.TryGetValue(key, out var index))
        {
            return index;
        }

        index = new MobAttackIndex();
        index.Load(ReadLocked(PathFor(key)));
        _indexes[key] = index;
        return index;
    }

    private string PathFor(string sanitizedServer) =>
        Path.Combine(_root, $"{sanitizedServer}.json");

    /// <summary>
    /// The server name comes from a filename the game wrote, so it is already
    /// tame — but it reaches us as a request-shaped string, and a store that
    /// builds paths from it should not be the thing that trusts it.
    /// </summary>
    private static string Sanitize(string value)
    {
        var cleaned = new string(value
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .ToArray());
        return cleaned.Length > 0 ? cleaned : "unknown";
    }

    /// <summary>
    /// Persists, and shrugs off a disk that would not take it — this runs from
    /// the same loop that expires fights, and a locked file must cost the user
    /// their mob profiles, not their session.
    /// </summary>
    private void WriteLocked(string server, MobAttackIndex index)
    {
        var path = PathFor(Sanitize(server));
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(temp, JsonSerializer.Serialize(index.Snapshot()));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static List<MobAttackRecord> ReadLocked(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<MobAttackRecord>>(File.ReadAllText(path)) ?? []
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
