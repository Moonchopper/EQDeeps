using EQDeeps.Core.Events;
using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// A time frame picked off the fight list arrives as an explicit TimeRange.
/// Combat still has to aggregate per fight inside it — otherwise selecting a
/// stretch of pulls would quietly report a lower DPS than selecting the same
/// pulls individually, purely because the downtime between them got averaged
/// in. Progression sources take the range whole, which is what makes a range
/// worth having: XP and loot land between the pulls, not during them.
/// </summary>
public class ScopeRangeTests
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);

    private readonly IdentityRegistry _identity = new();
    private readonly RecordStore _records = new();
    private readonly FightTracker _tracker;
    private readonly QueryEngine _engine;

    public ScopeRangeTests()
    {
        _identity.AddVerifiedPlayer("Raider01");
        _tracker = new FightTracker(_identity);
        _engine = new QueryEngine(_records, _tracker, _identity, "Kizant");

        // Fight 1: 100 damage/s for 4 s, then the mob dies.
        for (var t = 0; t < 4; t++)
        {
            Add(t, new DamageEvent("Raider01", "An ice giant", 100, DamageKind.Melee, "Crushes"));
        }

        Add(4, new DeathEvent("An ice giant", "Raider01"));

        // 200 s of downtime — well past FightTimeout, so fight 2 is its own
        // fight — with XP and loot arriving in it, outside any fight span.
        Add(60, new ExperienceEvent(Percent: 5, Party: false));
        Add(90, new LootEvent("Raider01", "a rusty dagger", "An ice giant corpse", Copper: 12_000));

        // Fight 2: identical shape, same rate.
        for (var t = 204; t < 208; t++)
        {
            Add(t, new DamageEvent("Raider01", "A frost giant", 100, DamageKind.Melee, "Crushes"));
        }

        Add(208, new DeathEvent("A frost giant", "Raider01"));
    }

    private void Add(int t, GameEvent evt)
    {
        var timestamp = T0.AddSeconds(t);
        _records.Append(timestamp, evt);
        _tracker.Process(timestamp, evt);
    }

    private QuerySpec Spec(QuerySource source, QueryScope scope, string metric) => new()
    {
        Source = source,
        Scope = scope,
        GroupBy = [Dimension.Player],
        Metrics = [metric, "total", "activeSeconds"],
    };

    [Fact]
    public void CombatOverAFightRangeMatchesSelectingThoseFights()
    {
        var fights = _tracker.Fights;
        Assert.Equal(2, fights.Count);

        var byIds = _engine.Execute(Spec(
            QuerySource.Damage,
            new QueryScope { FightIds = [fights[0].Id, fights[1].Id] },
            "dps"));

        // The same two pulls expressed as the wall-clock range covering them,
        // which is what the fight list produces when used as a range selector.
        var byRange = _engine.Execute(Spec(
            QuerySource.Damage,
            new QueryScope
            {
                TimeRanges = [new TimeRange(fights[0].BeginTime, fights[1].LastDamageTime)],
            },
            "dps"));

        Assert.Equal(800, byIds.Totals["total"]);
        Assert.Equal(byIds.Totals["total"], byRange.Totals["total"]);

        var idsRow = byIds.Rows.Single();
        var rangeRow = byRange.Rows.Single();
        Assert.Equal(8, idsRow.Metrics["activeSeconds"]);
        Assert.Equal(idsRow.Metrics["activeSeconds"], rangeRow.Metrics["activeSeconds"]);
        Assert.Equal(100.0, idsRow.Metrics["dps"], precision: 10);
        Assert.Equal(idsRow.Metrics["dps"], rangeRow.Metrics["dps"], precision: 10);

        // Without subdivision the range would span 205 s of mostly downtime and
        // report roughly 4 DPS instead of 100 — the regression this guards.
        Assert.True(rangeRow.Metrics["dps"] > 50);
    }

    [Fact]
    public void ProgressionOverAFightRangeCountsWhatLandedBetweenThePulls()
    {
        var fights = _tracker.Fights;
        var range = new QueryScope
        {
            TimeRanges = [new TimeRange(fights[0].BeginTime, fights[1].LastDamageTime)],
        };

        // The XP tick and the loot both landed in the gap between the pulls.
        // Scoped to the fights themselves they are invisible; scoped to the
        // range that covers them they are counted.
        var xp = _engine.Execute(Spec(QuerySource.Experience, range, "xpPercent"));
        Assert.Equal(5, xp.Totals["xpPercent"]);

        var loot = _engine.Execute(Spec(QuerySource.Loot, range, "platinum"));
        Assert.Equal(12, loot.Totals["platinum"]);

        var byFights = _engine.Execute(Spec(
            QuerySource.Experience,
            new QueryScope { FightIds = [fights[0].Id, fights[1].Id] },
            "xpPercent"));
        Assert.Equal(0, byFights.Totals.GetValueOrDefault("xpPercent"));
    }
}
