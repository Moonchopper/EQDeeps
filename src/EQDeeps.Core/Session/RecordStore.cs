using EQDeeps.Core.Events;

namespace EQDeeps.Core.Sessions;

public readonly record struct TimedRecord(DateTime Timestamp, GameEvent Event);

/// <summary>
/// Append-only, time-ordered store of a session's parsed records — the query
/// engine's source of truth. Appends happen on the session's processing task
/// only; <see cref="Version"/> increments on every append so caches keyed by
/// (query, scope, version) can detect staleness. Range lookups binary-search the
/// mostly-monotonic timestamps (DST regressions make results approximate at the
/// boundary, matching the domain's tolerance).
/// </summary>
public sealed class RecordStore
{
    private readonly List<TimedRecord> _records = [];

    public int Count => _records.Count;

    public int Version { get; private set; }

    public TimedRecord this[int index] => _records[index];

    public void Append(DateTime timestamp, GameEvent evt)
    {
        _records.Add(new TimedRecord(timestamp, evt));
        Version++;
    }

    /// <summary>
    /// A copy of the records at <c>[from, to)</c>, for a caller that wants to
    /// work on them off the session gate — the checkpoint writer serializes a
    /// slice this way. Structs of a timestamp and a reference, so the copy is
    /// cheap and the records themselves are shared, immutable, and safe to
    /// read from any thread.
    /// </summary>
    public TimedRecord[] CopyRange(int from, int to)
    {
        var slice = new TimedRecord[Math.Max(0, to - from)];
        _records.CopyTo(from, slice, 0, slice.Length);
        return slice;
    }

    /// <summary>Records with <c>from &lt;= Timestamp &lt;= to</c>.</summary>
    public IEnumerable<TimedRecord> Range(DateTime from, DateTime to)
    {
        for (var i = LowerBound(from); i < _records.Count; i++)
        {
            var record = _records[i];
            if (record.Timestamp > to)
            {
                yield break;
            }

            yield return record;
        }
    }

    /// <summary>Index of the first record at or after <paramref name="timestamp"/>.</summary>
    public int LowerBound(DateTime timestamp)
    {
        int lo = 0, hi = _records.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_records[mid].Timestamp < timestamp)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
