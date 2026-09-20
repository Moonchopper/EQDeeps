using EQDeeps.Core.Events;
using EQDeeps.Core.Parsing;
using EQDeeps.Core.Sessions;

namespace EQDeeps.Core.Query;

/// <summary>
/// Which <see cref="InstanceZone"/> each record happened in, per ADR-022
/// Decision 1/3: "a record's zone is whatever the most recent
/// <see cref="ZoneEvent"/> before it, in the record store's ORDER, established
/// — a named entry opens a zone, a nameless one (a load screen, a login)
/// closes it without opening anything."
///
/// Keyed by <b>record-store index</b>, not by timestamp, unlike
/// <see cref="StanceTimeline"/> and <see cref="ContextTimeline"/>. Log
/// resolution is one second, so a purely time-keyed lookup cannot order a
/// zone line against a record stamped the same second — and on this game a
/// zone's first few lines routinely land in the same second as the "You have
/// entered" line itself. The record-store index is the log's own total order
/// with no such gap: every line is its own index, so there is never a tie to
/// break.
/// </summary>
public sealed class ZoneTimeline
{
    /// <summary>
    /// No known zone — before the log's first zone line, or between a load
    /// screen and the next entry. One definition, used by the engine, the
    /// docs and the tests (ADR-022 Decision 1). Deliberately distinct from
    /// <see cref="InstanceZone.OpenWorld"/>: that means "a real zone, no
    /// instance suffix"; this means "no zone is known at all".
    /// </summary>
    public const string Unknown = "(unknown)";

    /// <summary>
    /// One change of zone state, starting at <see cref="BeginIndex"/> in the
    /// record store and holding until the next span. <see cref="Zone"/> is
    /// null for a closed span (no zone known).
    /// </summary>
    public readonly record struct Span(int BeginIndex, InstanceZone? Zone);

    private readonly List<Span> _spans;

    private ZoneTimeline(List<Span> spans) => _spans = spans;

    public IReadOnlyList<Span> Spans => _spans;

    /// <summary>True when the log never named a zone — the dimension is moot.</summary>
    public bool IsEmpty => _spans.Count == 0;

    /// <summary>
    /// Builds the zone spans over the whole record stream. Every
    /// <see cref="ZoneEvent"/> counts, regardless of who caused it to be
    /// written — unlike stances, a zone line is not attributed to an actor at
    /// all, it is a fact about where the log owner's client currently is.
    /// </summary>
    public static ZoneTimeline Build(RecordStore records)
    {
        var spans = new List<Span>();
        for (var i = 0; i < records.Count; i++)
        {
            if (records[i].Event is not ZoneEvent zone)
            {
                continue;
            }

            if (zone.ZoneName is { Length: > 0 } name)
            {
                spans.Add(new Span(i, InstanceZone.Parse(name)));
            }
            else if (spans.Count > 0 && spans[^1].Zone is not null)
            {
                // A close only needs recording when a zone is actually open —
                // otherwise we are already unknown by the empty-list default,
                // and a redundant close span would just be dead weight.
                spans.Add(new Span(i, null));
            }
        }

        return new ZoneTimeline(spans);
    }

    /// <summary>
    /// The zone in force at <paramref name="index"/> — the last span whose
    /// <see cref="Span.BeginIndex"/> is at or before it — or null when the
    /// index precedes the first zone line entirely.
    /// </summary>
    public InstanceZone? ZoneAt(int index)
    {
        int lo = 0, hi = _spans.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_spans[mid].BeginIndex <= index)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found < 0 ? null : _spans[found].Zone;
    }
}
