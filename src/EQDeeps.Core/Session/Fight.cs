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
    internal Fight(int id, string name, DateTime beginTime)
    {
        Id = id;
        Name = name;
        BeginTime = beginTime;
        LastDamageTime = beginTime;
        LastActivityTime = beginTime;
    }

    public int Id { get; }

    /// <summary>The NPC name (fight key).</summary>
    public string Name { get; }

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
