using EQDeeps.Core.Events;
using EQDeeps.Core.Sessions;

namespace EQDeeps.Core.Query;

public enum TimelineItemKind
{
    Cast,
    Song,
    Interrupt,
    Fizzle,
    Ability,
    Buff,
    Fade,
    Death,
    Resist,
}

/// <summary>
/// One mark on the timeline. Instants (casts, abilities, deaths…) have a null
/// <see cref="End"/>; buff spans run [Start, End]. Spans are clipped to the
/// requested range — <see cref="StartsBefore"/>/<see cref="EndsAfter"/> say a
/// clipped edge continues beyond it.
/// </summary>
public sealed record TimelineItem(
    string Actor,
    TimelineItemKind Kind,
    string Label,
    DateTime Start,
    DateTime? End = null,
    bool StartsBefore = false,
    bool EndsAfter = false);

public sealed record TimelineResult(
    DateTime? RangeBegin,
    DateTime? RangeEnd,
    IReadOnlyList<TimelineItem> Items,
    int DataVersion);

/// <summary>
/// Assembles the per-entity event timeline (feature F11 groundwork and the
/// seed of the event-annotation system): discrete casts/abilities/deaths/
/// resists inside the scope, plus buff spans derived by pairing the owner's
/// "begin casting X" with the named wear-off "Your X spell has worn off
/// [of T]". Only cast→wear-off *pairs* become spans — without the spell
/// database we cannot tell a nuke from a buff, nor see fades of received
/// buffs (their messages carry emote text, not names). The spell DB later
/// upgrades this to true durations and received-buff tracking.
/// </summary>
public static class TimelineBuilder
{
    public static TimelineResult Build(
        RecordStore records, FightTracker fights, string character, QueryScope scope)
    {
        var version = records.Version + fights.Version;
        var union = ResolveScope(records, fights, scope);
        if (union.Segments.Count == 0)
        {
            return new TimelineResult(null, null, [], version);
        }

        var rangeBegin = union.Segments[0].Begin;
        var rangeEnd = union.Segments[^1].End;
        var items = new List<TimelineItem>();

        // Buff spans: pair the owner's cast starts with named wear-offs over the
        // whole record stream (a buff cast before the pull and fading mid-fight
        // must still pair), then keep spans that touch the range. A recast
        // before the wear-off refreshes the buff, so the earliest open cast
        // anchors the span. The wear-off names the buff's target; the cast line
        // does not, so two same-spell buffs on different targets share one span
        // and the second fade falls back to an instant Fade mark.
        var pending = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var fades = new List<TimelineItem>();
        for (var i = 0; i < records.Count; i++)
        {
            var (timestamp, evt) = records[i];
            if (evt is CastEvent { Kind: CastKind.Begin, Spell: { } spell } cast &&
                string.Equals(cast.Caster, character, StringComparison.OrdinalIgnoreCase))
            {
                pending.TryAdd(spell, timestamp);
            }
            else if (evt is WearOffEvent wearOff)
            {
                if (pending.Remove(wearOff.Spell, out var castTime))
                {
                    if (castTime <= rangeEnd && timestamp >= rangeBegin)
                    {
                        items.Add(new TimelineItem(
                            wearOff.Target, TimelineItemKind.Buff, wearOff.Spell,
                            castTime < rangeBegin ? rangeBegin : castTime,
                            timestamp > rangeEnd ? rangeEnd : timestamp,
                            StartsBefore: castTime < rangeBegin,
                            EndsAfter: timestamp > rangeEnd));
                    }
                }
                else if (timestamp >= rangeBegin && timestamp <= rangeEnd)
                {
                    fades.Add(new TimelineItem(
                        wearOff.Target, TimelineItemKind.Fade, wearOff.Spell, timestamp));
                }
            }
        }

        // Instants: only records inside the scope's segments.
        var segmentIndex = 0;
        foreach (var (timestamp, evt) in records.Range(rangeBegin, rangeEnd))
        {
            while (segmentIndex < union.Segments.Count && union.Segments[segmentIndex].End < timestamp)
            {
                segmentIndex++;
            }

            if (segmentIndex >= union.Segments.Count || timestamp < union.Segments[segmentIndex].Begin)
            {
                continue;
            }

            var item = evt switch
            {
                CastEvent { Kind: CastKind.Begin } c => new TimelineItem(
                    c.Caster, c.Song ? TimelineItemKind.Song : TimelineItemKind.Cast,
                    c.Spell ?? "(unknown)", timestamp),
                CastEvent { Kind: CastKind.Interrupted } c => new TimelineItem(
                    c.Caster, TimelineItemKind.Interrupt, c.Spell ?? "(unknown)", timestamp),
                CastEvent { Kind: CastKind.Fizzle } c => new TimelineItem(
                    c.Caster, TimelineItemKind.Fizzle, c.Spell ?? "(unknown)", timestamp),
                AbilityEvent a => new TimelineItem(
                    a.User, TimelineItemKind.Ability, a.Ability, timestamp),
                DeathEvent d => new TimelineItem(
                    d.Victim, TimelineItemKind.Death,
                    d.Killer is null ? "died" : $"slain by {d.Killer}", timestamp),
                ResistEvent r => new TimelineItem(
                    r.Resister ?? r.Caster, TimelineItemKind.Resist, r.Spell, timestamp),
                _ => null,
            };

            if (item is not null)
            {
                items.Add(item);
            }
        }

        items.AddRange(fades);
        items.Sort((a, b) => a.Start.CompareTo(b.Start));
        return new TimelineResult(rangeBegin, rangeEnd, items, version);
    }

    /// <summary>Same scope semantics as the query engine's merged-range sources.</summary>
    private static TimeSegments ResolveScope(RecordStore records, FightTracker fights, QueryScope scope)
    {
        var union = new TimeSegments();
        if (scope.LastSeconds is > 0 and var lastSeconds)
        {
            if (records.Count > 0)
            {
                var latest = records[records.Count - 1].Timestamp;
                union.Add(latest.AddSeconds(-(lastSeconds - 1)), latest);
            }

            return union;
        }

        if (scope.TimeRanges is { Count: > 0 } explicitRanges)
        {
            foreach (var range in explicitRanges)
            {
                union.Add(range.Begin, range.End);
            }
        }
        else
        {
            var wanted = scope.FightIds is { Count: > 0 } ids ? new HashSet<int>(ids) : null;
            foreach (var fight in fights.Fights)
            {
                if (wanted is null || wanted.Contains(fight.Id))
                {
                    union.Add(fight.BeginTime, fight.LastDamageTime);
                }
            }
        }

        return union.Trim(scope.SkipFirstSeconds, scope.MaxSeconds);
    }
}
