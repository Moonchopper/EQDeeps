using EQDeeps.Core.Events;
using EQDeeps.Core.Parsing;
using EQDeeps.Core.Sessions;

namespace EQDeeps.Core.Query;

/// <summary>
/// The log owner's stance as a function of time.
///
/// The log records switches, not durations: "You assume a defensive stance."
/// says what changed, never for how long. Stances are exclusive, so a switch
/// implicitly ends the one before it and the spans tile the whole session with
/// no gaps and no overlaps — which is exactly what makes "damage per second
/// while in this stance" answerable at all.
///
/// Only the owner's stance is knowable. Other players' switches are not in the
/// log (their client wrote them, in their log), so records belonging to anyone
/// else are keyed to <see cref="NotTracked"/> rather than being quietly
/// attributed to whatever stance the owner happened to be holding.
/// </summary>
public sealed class StanceTimeline
{
    /// <summary>Before the first switch in the log: no idea, and honest about it.</summary>
    public const string Unknown = StanceParser.Unknown;

    /// <summary>Somebody else's record — their stance was never in this log.</summary>
    public const string NotTracked = "(not you)";

    /// <summary>One stretch of held stance, [Begin, End] inclusive.</summary>
    public readonly record struct Span(DateTime Begin, DateTime End, string Stance);

    private readonly List<Span> _spans;

    public StanceTimeline(List<Span> spans)
    {
        _spans = spans;
    }

    public IReadOnlyList<Span> Spans => _spans;

    /// <summary>True when the log never named a stance — the whole feature is moot.</summary>
    public bool IsEmpty => _spans.Count == 0;

    /// <summary>
    /// Builds the owner's stance spans over the whole record stream. Switches
    /// by anyone else are ignored: an NPC assuming a defensive stance is
    /// flavour text, not the player's state.
    /// </summary>
    public static StanceTimeline Build(RecordStore records, string character)
    {
        var changes = new List<(DateTime At, string Stance)>();
        for (var i = 0; i < records.Count; i++)
        {
            var (timestamp, evt) = records[i];
            if (evt is StanceEvent stance &&
                string.Equals(stance.Player, character, StringComparison.OrdinalIgnoreCase))
            {
                // Two switches in the same second: the later one wins, since
                // that is the state anything after it was fought under.
                if (changes.Count > 0 && changes[^1].At == timestamp)
                {
                    changes[^1] = (timestamp, stance.Stance);
                }
                else
                {
                    changes.Add((timestamp, stance.Stance));
                }
            }
        }

        var spans = new List<Span>();
        if (changes.Count == 0 || records.Count == 0)
        {
            return new StanceTimeline(spans);
        }

        // Everything before the first switch was fought in *something*, we just
        // don't know what. It gets its own span so that damage is never dropped
        // from the parse — only labelled honestly.
        var firstRecord = records[0].Timestamp;
        if (changes[0].At > firstRecord)
        {
            spans.Add(new Span(firstRecord, changes[0].At.AddSeconds(-1), Unknown));
        }

        var lastRecord = records[records.Count - 1].Timestamp;
        for (var i = 0; i < changes.Count; i++)
        {
            var end = i + 1 < changes.Count ? changes[i + 1].At.AddSeconds(-1) : lastRecord;
            if (end >= changes[i].At)
            {
                spans.Add(new Span(changes[i].At, end, changes[i].Stance));
            }
        }

        return new StanceTimeline(spans);
    }

    /// <summary>
    /// Index of the span covering <paramref name="timestamp"/>, or -1 when the
    /// timestamp falls outside every span (before the first record).
    /// </summary>
    public int IndexAt(DateTime timestamp)
    {
        int lo = 0, hi = _spans.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_spans[mid].End < timestamp)
            {
                lo = mid + 1;
            }
            else if (_spans[mid].Begin > timestamp)
            {
                hi = mid - 1;
            }
            else
            {
                return mid;
            }
        }

        return -1;
    }

    /// <summary>
    /// Index of the first span that has not already ended by
    /// <paramref name="timestamp"/> — the entry point for walking the spans
    /// that overlap a range, without scanning the ones behind it.
    /// </summary>
    public int FirstEndingAtOrAfter(DateTime timestamp)
    {
        int lo = 0, hi = _spans.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_spans[mid].End < timestamp)
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

    public string StanceAt(DateTime timestamp)
    {
        var index = IndexAt(timestamp);
        return index < 0 ? Unknown : _spans[index].Stance;
    }
}
