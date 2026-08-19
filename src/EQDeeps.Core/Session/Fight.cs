using EQDeeps.Core.Parsing;

namespace EQDeeps.Core.Sessions;

/// <summary>Running totals for one actor within one side of a fight.</summary>
public sealed class ActorTotals
{
    public long Total { get; internal set; }

    public int Hits { get; internal set; }
}

/// <summary>
/// One engagement with one NPC, keyed by NPC name and never merged with a later
/// pull of the same name. "Damage" is the players' side (dealt to the NPC);
/// "tanking" is the NPC's side (dealt by it). Holds lightweight running totals
/// and per-second series for the fight list and live meter; full counter bags
/// are computed by the query engine from the record store over
/// [<see cref="BeginTime"/>, <see cref="LastDamageTime"/>].
/// </summary>
public sealed class Fight
{
    internal Fight(int id, string name, DateTime beginTime, InstanceZone? zone = null)
    {
        Id = id;
        Name = name;
        BeginTime = beginTime;
        LastDamageTime = beginTime;
        LastActivityTime = beginTime;
        Zone = zone;
    }

    public int Id { get; }

    /// <summary>The NPC name (fight key).</summary>
    public string Name { get; }

    /// <summary>
    /// Where this was fought, split into place and instance difficulty — null
    /// until the log has said, which on a file opened mid-session is every
    /// fight up to the first zone line. An instance's difficulty rescales the
    /// mobs in it, so this is half of what identifies "the same mob" across
    /// fights; the name alone is not.
    /// </summary>
    public InstanceZone? Zone { get; }

    public DateTime BeginTime { get; }

    /// <summary>Last combat-record time — the fight's effective end for stats.</summary>
    public DateTime LastDamageTime { get; internal set; }

    /// <summary>Last time anything referenced the fight (taunts extend this, not LastDamageTime).</summary>
    public DateTime LastActivityTime { get; internal set; }

    public bool Dead { get; internal set; }

    public bool Closed { get; internal set; }

    public bool HasDamage { get; internal set; }

    public long DamageTotal { get; internal set; }

    public long TankingTotal { get; internal set; }

    public int TauntCount { get; internal set; }

    /// <summary>
    /// The tracker's <see cref="FightTracker.Version"/> as of this fight's
    /// last change — what a live push is cut against, so a raid's worth of
    /// closed fights is not re-sent every time the open one takes a hit.
    /// </summary>
    public int Version { get; internal set; }

    /// <summary>Damage dealt to the NPC, by raw actor name (pet rollup is query-time).</summary>
    public Dictionary<string, ActorTotals> DamageByActor { get; } = new(StringComparer.Ordinal);

    /// <summary>Damage dealt by the NPC, by defender name.</summary>
    public Dictionary<string, ActorTotals> TankingByDefender { get; } = new(StringComparer.Ordinal);

    /// <summary>Per-second landed totals: (players → NPC, NPC → players).</summary>
    public SortedDictionary<DateTime, SecondTotals> Seconds { get; } = [];

    public TimeSpan Duration => LastDamageTime - BeginTime + TimeSpan.FromSeconds(1);
}

public struct SecondTotals
{
    public long Damage;
    public long Tanking;
}
