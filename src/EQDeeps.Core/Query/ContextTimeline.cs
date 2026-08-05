using EQDeeps.Core.Events;
using EQDeeps.Core.Sessions;

namespace EQDeeps.Core.Query;

/// <summary>A stretch of time over which one thing stayed true.</summary>
public sealed record ContextSpan(TimeRange Range, string Label);

/// <summary>
/// Where the character was and what level they were, as spans a chart can draw
/// behind its data.
///
/// Fight bands already answer "was I fighting". These answer the two questions
/// a reader asks next about a trough or a step in the numbers: where was I, and
/// was I the same character then. A DPS floor that doubles halfway through a
/// log is a different fact if the level went up in the same hour, and an XP
/// rate is not comparable across two zones.
///
/// Both are step functions over the record stream — a value holds until
/// something says otherwise — but they are read from very different evidence:
///
///  * ZONE is stated on every change. "You have entered X." is exact, and the
///    LOADING line before it means the old zone has already stopped being
///    true, so the load screen belongs to neither and is left as a gap.
///  * LEVEL is stated only on the way UP. Dying can cost a level and the
///    client writes nothing when it does, so a level here is "the last one
///    announced", which a de-level leaves too high until the next ding. A
///    /who the player typed is the other source and a better one — it observes
///    the level rather than inferring it — so a self-/who line is taken as
///    authoritative wherever one exists.
///
/// Spans are clipped to presence, for the same reason fights are not drawn
/// across the night: the zone you logged out in is not a zone you spent nine
/// hours in, and a strip that says otherwise is worse than no strip.
/// </summary>
public sealed class ContextTimeline
{
    public ContextTimeline(IReadOnlyList<ContextSpan> zones, IReadOnlyList<ContextSpan> levels)
    {
        Zones = zones;
        Levels = levels;
    }

    /// <summary>Named zones, in order, none overlapping.</summary>
    public IReadOnlyList<ContextSpan> Zones { get; }

    /// <summary>Levels, in order, none overlapping.</summary>
    public IReadOnlyList<ContextSpan> Levels { get; }

    public static ContextTimeline Empty { get; } = new([], []);

    /// <param name="character">
    /// The log's owner. A /who prints everyone in the zone, so this is what
    /// picks the one line among them that is about this character.
    /// </param>
    public static ContextTimeline Build(RecordStore records, string character)
    {
        if (records.Count == 0)
        {
            return Empty;
        }

        var presence = PresenceTimeline.Build(records);
        var zones = new StepBuilder();
        var levels = new StepBuilder();
        // Whether the first thing that fixed a level was a /who rather than a
        // ding, which decides whether it may be read backwards (see below).
        var firstLevelObserved = false;
        var anyLevel = false;

        for (var i = 0; i < records.Count; i++)
        {
            var (timestamp, evt) = records[i];
            switch (evt)
            {
                // A named entry opens a zone; a transition (LOADING) or a login
                // closes the old one without opening anything, because what is
                // true during a load screen is "not there yet".
                case ZoneEvent zone:
                    if (zone.ZoneName is { Length: > 0 } name)
                    {
                        zones.Observe(timestamp, name);
                    }
                    else
                    {
                        zones.Close(timestamp);
                    }

                    break;

                // A ding fixes the level from that moment on, and says nothing
                // about what it was before — so it can never be backdated.
                case LevelEvent level:
                    anyLevel = true;
                    levels.Observe(timestamp, level.Level.ToString());
                    break;

                // Every /who line carries a level; only the owner's is theirs.
                case WhoEvent { Level: { } seen } who
                    when string.Equals(who.Player, character, StringComparison.OrdinalIgnoreCase):
                    firstLevelObserved |= !anyLevel;
                    anyLevel = true;
                    levels.Observe(timestamp, seen.ToString());
                    break;
            }
        }

        var last = records[records.Count - 1].Timestamp;
        // A /who reports a level that was already true, and a ding would have
        // been logged if it had changed on the way here — so the first one read
        // that way is read backwards to the start of the log as well as
        // forwards. Without it a character who types /who at nine in the
        // evening has no level at all for everything before that, which is the
        // majority of most logs. A DING is the opposite: it says the level
        // began at that moment, so it is never backdated.
        //
        // The gap this cannot close is a de-level, which the client never
        // writes down. Reading a /who backwards over one would show the level
        // the player ended on rather than the one they were.
        var backdate = firstLevelObserved ? records[0].Timestamp : (DateTime?)null;
        return new ContextTimeline(
            zones.Build(last, presence),
            levels.Build(last, presence, backdate));
    }

    /// <summary>
    /// Accumulates one step function: a value holds from the moment it was
    /// observed until something else is observed, or until the log stops.
    /// Repeating the current value is not a change — a /who typed three times
    /// in a camp is one span, not three.
    /// </summary>
    private sealed class StepBuilder
    {
        private readonly List<ContextSpan> _spans = [];
        private string? _current;
        private DateTime _since;

        public void Observe(DateTime at, string label)
        {
            if (_current == label)
            {
                return;
            }

            Close(at);
            _current = label;
            _since = at;
        }

        public void Close(DateTime at)
        {
            if (_current is not null && at > _since)
            {
                _spans.Add(new ContextSpan(new TimeRange(_since, at), _current));
            }

            _current = null;
        }

        public List<ContextSpan> Build(
            DateTime last, PresenceTimeline presence, DateTime? backdateFirstTo = null)
        {
            Close(last);

            if (backdateFirstTo is { } from && _spans.Count > 0 && _spans[0].Range.Begin > from)
            {
                _spans[0] = _spans[0] with
                {
                    Range = new TimeRange(from, _spans[0].Range.End),
                };
            }

            var clipped = new List<ContextSpan>();
            foreach (var span in _spans)
            {
                foreach (var piece in presence.Intersect(span.Range))
                {
                    clipped.Add(new ContextSpan(piece, span.Label));
                }
            }

            return clipped;
        }
    }
}
