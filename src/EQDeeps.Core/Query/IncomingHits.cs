using EQDeeps.Core.Events;
using EQDeeps.Core.Sessions;

namespace EQDeeps.Core.Query;

/// <summary>
/// One swing a mob threw at the players' side, exactly as the log recorded it.
/// </summary>
/// <param name="Outcome">
/// Landed damage is <see cref="DamageKind.Melee"/> and friends; an avoided
/// swing is its own kind with a zero <paramref name="Amount"/>. Both are here:
/// a feed that only showed the hits would answer "what killed me" with the half
/// of the story that has numbers in it.
/// </param>
/// <param name="DefenderOwner">Set when the defender is a pet, so a feed can roll it up.</param>
/// <param name="Fight">The fight this fell inside, when it fell inside one.</param>
public sealed record IncomingHit(
    DateTime At,
    string Attacker,
    string Defender,
    string? DefenderOwner,
    string Skill,
    DamageKind Outcome,
    long Amount,
    HitModifiers Modifiers,
    bool Spell,
    string? Fight);

/// <summary>
/// The tail of the incoming-damage stream over a scope.
/// </summary>
/// <param name="Hits">Newest last, so a feed reads top-down like the log does.</param>
/// <param name="Total">
/// How many fell in the scope before the tail was taken, so the panel can say
/// what it is not showing rather than implying the list is all of it.
/// </param>
public sealed record IncomingHitsResult(
    DateTime? RangeBegin,
    DateTime? RangeEnd,
    IReadOnlyList<IncomingHit> Hits,
    int Total,
    int DataVersion);

/// <summary>
/// The raw feed behind the incoming-damage breakdown (F26): what hit you, for
/// how much, in what order.
///
/// <para>This is deliberately <b>not</b> a <see cref="QuerySpec"/>. Every table
/// in the app is an aggregation, and the whole point of this one is that it is
/// not aggregated — the sequence is the information. "Three parries, then a
/// 900-point crush, then a death" is a story the same three rows grouped by
/// skill cannot tell, and the rule that a special-case rendering path should
/// probably have been a query does not apply to a view whose subject is
/// ordering.</para>
///
/// <para>The tanking source answers the aggregate half of the same question and
/// keeps answering it; this fills the gap under it.</para>
/// </summary>
public static class IncomingHitsBuilder
{
    /// <summary>What a caller gets without asking, and enough to read a death back.</summary>
    public const int DefaultLimit = 200;

    /// <summary>
    /// Ceiling on one response. A raid night holds hundreds of thousands of
    /// incoming records and no feed is read that far; the cap keeps a scope of
    /// "everything" from serializing a log back over HTTP.
    /// </summary>
    public const int MaxLimit = 2000;

    /// <param name="defenders">
    /// Restrict to these defenders (pets resolve through their owners), or null
    /// for everyone the log saw being hit.
    /// </param>
    public static IncomingHitsResult Build(
        RecordStore records,
        FightTracker fights,
        IdentityRegistry identity,
        QueryScope scope,
        int limit = DefaultLimit,
        IReadOnlyCollection<string>? defenders = null)
    {
        var version = records.Version + fights.Version;
        var union = ResolveScope(records, fights, scope);
        if (union.Segments.Count == 0)
        {
            return new IncomingHitsResult(null, null, [], 0, version);
        }

        limit = Math.Clamp(limit, 1, MaxLimit);
        var wanted = defenders is { Count: > 0 }
            ? new HashSet<string>(defenders, StringComparer.OrdinalIgnoreCase)
            : null;

        // A ring buffer rather than a list that gets sorted and truncated: the
        // scope can be an entire evening, and holding every incoming record of
        // one to hand back two hundred of them is the kind of thing that only
        // shows up on somebody else's machine.
        var tail = new IncomingHit[limit];
        var seen = 0;

        foreach (var segment in union.Segments)
        {
            foreach (var (timestamp, evt) in records.Range(segment.Begin, segment.End))
            {
                if (evt is not DamageEvent damage ||
                    damage.Attacker is not { Length: > 0 } attacker ||
                    damage.AttackerIsSpell)
                {
                    continue;
                }

                // Incoming is decided the way the tanking source decides it: the
                // attacker has to be definitively an NPC, and the defender only
                // has to not be one. Most group members never say a word, join a
                // raid or appear in a /who, so demanding a verified player on
                // the receiving end would quietly drop the defenders a tank
                // most wants to see — while the article-and-spaces test still
                // keeps a mob-on-mob swing out of a feed about the party.
                if (identity.IsPlayerSide(attacker) ||
                    !identity.IsDefinitelyNpc(attacker) ||
                    identity.IsDefinitelyNpc(damage.Defender))
                {
                    continue;
                }

                var owner = damage.DefenderOwner ?? identity.OwnerOf(damage.Defender);
                if (wanted is not null &&
                    !wanted.Contains(damage.Defender) &&
                    (owner is null || !wanted.Contains(owner)))
                {
                    continue;
                }

                tail[seen % limit] = new IncomingHit(
                    timestamp,
                    attacker,
                    damage.Defender,
                    owner,
                    damage.SubType is { Length: > 0 } sub ? sub : damage.Kind.ToString(),
                    damage.Kind,
                    damage.Amount,
                    damage.Modifiers,
                    damage.Kind is DamageKind.DirectDamage or DamageKind.DamageOverTime
                        or DamageKind.DamageShield or DamageKind.Other,
                    FightAt(fights, timestamp, attacker));
                seen++;
            }
        }

        return new IncomingHitsResult(
            union.Segments[0].Begin,
            union.Segments[^1].End,
            Drain(tail, seen),
            seen,
            version);
    }

    /// <summary>Unrolls the ring into log order, oldest first.</summary>
    private static List<IncomingHit> Drain(IncomingHit[] tail, int seen)
    {
        var kept = Math.Min(seen, tail.Length);
        var hits = new List<IncomingHit>(kept);
        var start = seen - kept;
        for (var i = 0; i < kept; i++)
        {
            hits.Add(tail[(start + i) % tail.Length]);
        }

        return hits;
    }

    /// <summary>
    /// Which fight a hit belongs to. Matched on the attacker's name as well as
    /// the instant, because fights overlap — a second pull landing mid-fight
    /// would otherwise label every hit with whichever fight the scan reached
    /// first.
    /// </summary>
    private static string? FightAt(FightTracker fights, DateTime at, string attacker)
    {
        foreach (var fight in fights.Fights)
        {
            if (fight.BeginTime <= at && at <= fight.LastDamageTime &&
                string.Equals(fight.Name, attacker, StringComparison.OrdinalIgnoreCase))
            {
                return fight.Name;
            }
        }

        return null;
    }

    /// <summary>Same scope semantics as the timeline and the merged-range sources; shared with the item-mention feed (F29).</summary>
    internal static TimeSegments ResolveScope(
        RecordStore records, FightTracker fights, QueryScope scope)
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
