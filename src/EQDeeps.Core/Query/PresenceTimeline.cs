using EQDeeps.Core.Events;
using EQDeeps.Core.Sessions;

namespace EQDeeps.Core.Query;

/// <summary>
/// When the player was actually at the keyboard — the spans a log covers, with
/// the time between play sessions cut out.
///
/// A log file is a diary with the nights missing: it runs for months, and the
/// gap between Tuesday's last kill and Wednesday's first one is written exactly
/// like the gap between two pulls. Anything that measures a DURATION from the
/// record stream has to know the difference, or a stance held at logout is
/// "held" for nine hours and a plat-per-hour over a month-old log is divided by
/// the month.
///
/// Two signals, because neither is sufficient alone:
///
///  * "Welcome to EverQuest!" is printed once per entry into the world, so it
///    is an exact login. It cannot mark the logout, though — the client writes
///    nothing on the way out, and a crash writes less than that.
///  * A long quiet stretch ends a session at its last record. This is what
///    supplies the missing logout, and it also covers logs the marker never
///    reached: rotated files, logging switched off mid-session, a client killed
///    outright.
///
/// The quiet threshold is set against how quiet a live log actually goes. Over
/// 1.9 million lines of real play, 99.9% of consecutive records land within 25
/// seconds of each other and only 57 gaps exceeded a minute — of which the ones
/// past ten minutes were, without exception, absences of an hour or more. Ten
/// minutes is comfortably clear of the noise and comfortably under the shortest
/// real absence.
///
/// Being wrong in one direction is much cheaper than the other, and this errs
/// deliberately: a session ends at its last RECORD, not at the unknowable
/// moment the player actually quit, so presence is under-counted by however
/// long they idled before leaving. Under-counting shortens a duration slightly;
/// over-counting adds entire nights to it.
/// </summary>
public sealed class PresenceTimeline
{
    /// <summary>Quiet longer than this is treated as "gone", not "idle".</summary>
    public static readonly TimeSpan MaxQuietGap = TimeSpan.FromMinutes(10);

    private readonly List<TimeRange> _spans;

    public PresenceTimeline(List<TimeRange> spans)
    {
        _spans = spans;
    }

    /// <summary>Play sessions, in order, none touching.</summary>
    public IReadOnlyList<TimeRange> Spans => _spans;

    public bool IsEmpty => _spans.Count == 0;

    public static PresenceTimeline Build(RecordStore records) =>
        Build(records, MaxQuietGap);

    /// <summary>Threshold-injectable for tests; production uses <see cref="MaxQuietGap"/>.</summary>
    public static PresenceTimeline Build(RecordStore records, TimeSpan maxQuietGap)
    {
        var spans = new List<TimeRange>();
        if (records.Count == 0)
        {
            return new PresenceTimeline(spans);
        }

        var begin = records[0].Timestamp;
        var previous = begin;
        for (var i = 1; i < records.Count; i++)
        {
            var (timestamp, evt) = records[i];

            // A login always starts a session, however brief the gap: someone
            // swapping characters is back at the character-select screen even
            // if it only took them a minute.
            var login = evt is ZoneEvent { Welcome: true };
            if (login || timestamp - previous >= maxQuietGap)
            {
                spans.Add(new TimeRange(begin, previous));
                begin = timestamp;
            }

            previous = timestamp;
        }

        spans.Add(new TimeRange(begin, previous));
        return new PresenceTimeline(spans);
    }

    /// <summary>
    /// The parts of <paramref name="range"/> the player was present for. An
    /// empty result means the whole range fell between sessions.
    /// </summary>
    public List<TimeRange> Intersect(TimeRange range)
    {
        var result = new List<TimeRange>();
        foreach (var span in _spans)
        {
            if (span.End < range.Begin)
            {
                continue;
            }

            if (span.Begin > range.End)
            {
                break; // spans are ordered; nothing further can overlap
            }

            result.Add(new TimeRange(
                span.Begin > range.Begin ? span.Begin : range.Begin,
                span.End < range.End ? span.End : range.End));
        }

        return result;
    }

    /// <summary>Seconds of <paramref name="range"/> spent logged in.</summary>
    public double SecondsWithin(TimeRange range)
    {
        double total = 0;
        foreach (var piece in Intersect(range))
        {
            total += piece.TotalSeconds;
        }

        return total;
    }
}
