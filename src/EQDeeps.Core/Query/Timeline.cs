using EQDeeps.Core.Spells;
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

    /// <summary>A held combat stance; spans, like <see cref="Buff"/>.</summary>
    Stance,
}

/// <summary>
/// One mark on the timeline. Instants (casts, abilities, deaths…) have a null
/// <see cref="End"/>; buff spans run [Start, End]. Spans are clipped to the
/// requested range — <see cref="StartsBefore"/>/<see cref="EndsAfter"/> say a
/// clipped edge continues beyond it.
/// </summary>
/// <summary>What a cast turned out to do, for sizing its mark.</summary>
public enum TimelineEffect
{
    None,
    Damage,
    Heal,
}

public sealed record TimelineItem(
    string Actor,
    TimelineItemKind Kind,
    string Label,
    DateTime Start,
    DateTime? End = null,
    bool StartsBefore = false,
    bool EndsAfter = false,
    /// <summary>Total landed by this cast, when it could be paired. Null otherwise.</summary>
    double? Amount = null,
    TimelineEffect Effect = TimelineEffect.None);

public sealed record TimelineResult(
    DateTime? RangeBegin,
    DateTime? RangeEnd,
    IReadOnlyList<TimelineItem> Items,
    int DataVersion);

/// <summary>
/// Assembles the per-entity event timeline (feature F11 groundwork and the
/// seed of the event-annotation system): discrete casts/abilities/deaths/
/// resists inside the scope, plus buff spans.
///
/// <para>A span is opened by the owner's "begin casting X" or by a buff
/// landing on someone (<see cref="LandedEvent"/>, which the player's own spell
/// files make readable), and closed by the matching wear-off. Received buffs
/// are therefore visible now, which they were not while the only evidence was
/// a cast line the owner had to have made.</para>
///
/// <para>A span with no fade in the log used to run to the end of the range.
/// Where the spell files give a duration and the owner's level is known — so,
/// for spells the owner cast on themselves — the span now ends when the buff
/// actually would have. Everything else still stops at the range, because a
/// guess about someone else's caster level would be a worse answer than an
/// honest open end.</para>
/// </summary>
public static class TimelineBuilder
{
    public static TimelineResult Build(
        RecordStore records, FightTracker fights, string character, QueryScope scope,
        SpellBook? spellBook = null)
    {
        var spells = spellBook ?? SpellBook.Empty;
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
        // Spans the owner cast on themselves, where a predicted end is honest:
        // we know the spell and we know what level they were at the time.
        var selfCast = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fades = new List<TimelineItem>();
        var ownerLevel = 0;
        for (var i = 0; i < records.Count; i++)
        {
            var (timestamp, evt) = records[i];
            if (evt is LevelEvent level)
            {
                ownerLevel = level.Level;
            }
            else if (evt is CastEvent { Kind: CastKind.Begin, Spell: { } spell } cast &&
                string.Equals(cast.Caster, character, StringComparison.OrdinalIgnoreCase))
            {
                if (pending.TryAdd(spell, timestamp) && ownerLevel > 0)
                {
                    selfCast[spell] = ownerLevel;
                }
            }
            else if (evt is LandedEvent { Spell: { } landed } landing)
            {
                // A buff landing opens a span in its own right. The cast that
                // caused it may be the owner's — already pending, and the
                // earlier moment — or someone else's, which the log never
                // showed us until now.
                pending.TryAdd(landed, timestamp);
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

        // Buffs that never faded in the log. A duration is only applied to a
        // spell the owner cast themselves: the formula needs the *caster's*
        // level, and using the owner's for someone else's buff would invent a
        // number. The rest are left to the old behaviour of simply not being
        // drawn as spans, which is what an unknown end honestly looks like.
        foreach (var (spell, castTime) in pending)
        {
            if (!selfCast.TryGetValue(spell, out var castLevel) ||
                spells.DurationOf(spell, castLevel) is not { } duration ||
                duration <= TimeSpan.Zero)
            {
                continue;
            }

            var end = castTime + duration;
            if (end < rangeBegin || castTime > rangeEnd)
            {
                continue;
            }

            items.Add(new TimelineItem(
                character, TimelineItemKind.Buff, spell,
                castTime < rangeBegin ? rangeBegin : castTime,
                end > rangeEnd ? rangeEnd : end,
                StartsBefore: castTime < rangeBegin,
                EndsAfter: end > rangeEnd));
        }

        // Stance spans: unlike buffs these need no pairing, because a switch
        // ends the previous stance by definition. Clipped to the range and kept
        // whether or not the switch itself happened inside it — a stance
        // assumed before the pull is still the stance the pull was fought in.
        var presence = PresenceTimeline.Build(records);
        foreach (var span in StanceTimeline.Build(records, character).Spans)
        {
            if (span.End < rangeBegin || span.Begin > rangeEnd)
            {
                continue;
            }

            var clipped = new TimeRange(
                span.Begin < rangeBegin ? rangeBegin : span.Begin,
                span.End > rangeEnd ? rangeEnd : span.End);

            // Split on the play sessions, so a stance held at logout stops at
            // the last thing that happened rather than drawing a bar across the
            // night — the picture has to agree with the number beside it.
            foreach (var piece in presence.Intersect(clipped))
            {
                items.Add(new TimelineItem(
                    character, TimelineItemKind.Stance, span.Stance,
                    piece.Begin, piece.End,
                    StartsBefore: span.Begin < piece.Begin,
                    EndsAfter: span.End > piece.End));
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
        AttachCastAmounts(records, items, rangeEnd);
        items.Sort((a, b) => a.Start.CompareTo(b.Start));
        return new TimelineResult(rangeBegin, rangeEnd, items, version);
    }

    /// <summary>
    /// How long after "begins casting X" the result may still arrive. Long
    /// enough for a slow cast plus travel time, short enough that it is still
    /// plausibly THIS cast's doing.
    /// </summary>
    private static readonly TimeSpan LandingWindow = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Joins each cast to what it did, so the UI can size the mark by it.
    ///
    /// The cast line fires when the spell STARTS, and carries no numbers — the
    /// damage or healing arrives later as its own record. So the amount has to
    /// be inferred: sum everything that actor landed under that spell's name
    /// inside <see cref="LandingWindow"/>, stopping early if they recast it,
    /// since past that point the credit is ambiguous.
    ///
    /// Known and accepted: a damage-over-time spell is credited only with the
    /// ticks inside the window, not its lifetime total, and a multi-target
    /// spell sums every target it hit — which is what "what did that cast do"
    /// should mean. Casts that never land anything keep a null amount and the
    /// UI draws them at its base size rather than at zero.
    /// </summary>
    private static void AttachCastAmounts(
        RecordStore records, List<TimelineItem> items, DateTime rangeEnd)
    {
        var landings = new Dictionary<(string Actor, string Spell), List<(DateTime At, double Amount, bool Heal)>>(
            CastKeyComparer.Instance);

        void Land(string? actor, string? spell, DateTime at, double amount, bool heal)
        {
            if (actor is null || string.IsNullOrEmpty(spell) || amount <= 0)
            {
                return;
            }

            var key = (actor, spell);
            if (!landings.TryGetValue(key, out var list))
            {
                landings[key] = list = [];
            }

            list.Add((at, amount, heal));
        }

        // Reach past the range's end: a cast at the last second still lands
        // after it, and that result belongs to the cast the user can see.
        foreach (var (timestamp, evt) in records.Range(records.Count > 0 ? records[0].Timestamp : rangeEnd,
                     rangeEnd + LandingWindow))
        {
            switch (evt)
            {
                case DamageEvent { AttackerIsSpell: false } d when d.Kind is not DamageKind.DamageShield:
                    Land(d.Attacker, d.SubType, timestamp, d.Amount, heal: false);
                    break;
                case HealEvent h:
                    Land(h.Healer, h.Spell, timestamp, h.Landed, heal: true);
                    break;
            }
        }

        // A recast closes the previous cast's window early.
        var nextCast = new Dictionary<(string, string), List<DateTime>>(CastKeyComparer.Instance);
        foreach (var item in items)
        {
            if (item.Kind is TimelineItemKind.Cast or TimelineItemKind.Song)
            {
                var key = (item.Actor, item.Label);
                if (!nextCast.TryGetValue(key, out var times))
                {
                    nextCast[key] = times = [];
                }

                times.Add(item.Start);
            }
        }

        foreach (var times in nextCast.Values)
        {
            times.Sort();
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Kind is not (TimelineItemKind.Cast or TimelineItemKind.Song))
            {
                continue;
            }

            var key = (item.Actor, item.Label);
            if (!landings.TryGetValue(key, out var candidates))
            {
                continue;
            }

            var windowEnd = item.Start + LandingWindow;
            if (nextCast.TryGetValue(key, out var castTimes))
            {
                foreach (var other in castTimes)
                {
                    if (other > item.Start && other < windowEnd)
                    {
                        windowEnd = other;
                        break;
                    }
                }
            }

            double damage = 0;
            double healed = 0;
            foreach (var (at, amount, heal) in candidates)
            {
                if (at < item.Start || at >= windowEnd)
                {
                    continue;
                }

                if (heal)
                {
                    healed += amount;
                }
                else
                {
                    damage += amount;
                }
            }

            // A spell that both damages and heals (a lifetap) is reported as
            // whichever it did more of — one mark, one size, one scale.
            if (damage <= 0 && healed <= 0)
            {
                continue;
            }

            items[i] = damage >= healed
                ? item with { Amount = damage, Effect = TimelineEffect.Damage }
                : item with { Amount = healed, Effect = TimelineEffect.Heal };
        }
    }

    private sealed class CastKeyComparer : IEqualityComparer<(string Actor, string Spell)>
    {
        public static readonly CastKeyComparer Instance = new();

        public bool Equals((string Actor, string Spell) a, (string Actor, string Spell) b) =>
            string.Equals(a.Actor, b.Actor, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Spell, b.Spell, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Actor, string Spell) key) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Actor),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Spell));
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
