namespace EQDeeps.Core.Query;

/// <summary>
/// The classic views as ordinary QuerySpecs — proven editable in the query
/// builder because they ARE queries, not bespoke code paths (feature F4).
/// </summary>
public static class CannedQueries
{
    public static QuerySpec DamageSummary(IReadOnlyList<int>? fightIds = null) => new()
    {
        Source = QuerySource.Damage,
        Scope = new QueryScope { FightIds = fightIds },
        GroupBy = [Dimension.Player, Dimension.Spell],
    };

    public static QuerySpec HealingSummary(IReadOnlyList<int>? fightIds = null) => new()
    {
        Source = QuerySource.Healing,
        Scope = new QueryScope { FightIds = fightIds },
        GroupBy = [Dimension.Player, Dimension.Spell],
    };

    public static QuerySpec TankingSummary(IReadOnlyList<int>? fightIds = null) => new()
    {
        Source = QuerySource.Tanking,
        Scope = new QueryScope { FightIds = fightIds },
        GroupBy = [Dimension.Player, Dimension.Spell],
    };

    public static QuerySpec DpsOverTime(IReadOnlyList<int>? fightIds = null, int bucketSeconds = 1) => new()
    {
        Source = QuerySource.Damage,
        Scope = new QueryScope { FightIds = fightIds },
        GroupBy = [Dimension.Player],
        Metrics = ["total", "dps", "sdps"],
        BucketSeconds = bucketSeconds,
    };

    public static QuerySpec DeathLog(IReadOnlyList<int>? fightIds = null) => new()
    {
        Source = QuerySource.Deaths,
        Scope = new QueryScope { FightIds = fightIds },
        GroupBy = [Dimension.Player, Dimension.Target],
    };
}
