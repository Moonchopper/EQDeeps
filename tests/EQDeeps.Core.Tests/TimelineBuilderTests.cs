using EQDeeps.Core.Events;
using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// Timeline assembly over a hand-built scenario. Fight "An ice giant" runs
/// t2..t8; the owner's Spirit of Wolf is cast before the pull (t0) and fades
/// mid-fight (t5), Haste is cast mid-fight (t3) and outlives it (t20) — both
/// must clip to the range with the matching StartsBefore/EndsAfter flags.
/// </summary>
public class TimelineBuilderTests
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);

    private readonly RecordStore _records = new();
    private readonly FightTracker _tracker;

    public TimelineBuilderTests()
    {
        var identity = new IdentityRegistry();
        identity.AddVerifiedPlayer("Raider01");
        _tracker = new FightTracker(identity);

        Add(0, new CastEvent("Kizant", "Spirit of Wolf", CastKind.Begin));
        Add(2, new DamageEvent("Kizant", "An ice giant", 100, DamageKind.Melee, "Crushes"));
        Add(3, new CastEvent("Kizant", "Haste", CastKind.Begin));
        Add(3, new CastEvent("An ice giant", "Frost Breath", CastKind.Begin));
        Add(4, new AbilityEvent("Raider01", "Bestial Fury"));
        Add(5, new WearOffEvent("Spirit of Wolf", "Kizant"));
        Add(6, new WearOffEvent("Chloroplast", "Kizant"));
        Add(7, new ResistEvent("Kizant", "An ice giant", "Snare"));
        Add(8, new DamageEvent("Kizant", "An ice giant", 300, DamageKind.Melee, "Crushes"));
        Add(8, new DeathEvent("An ice giant", "Kizant"));
        Add(20, new WearOffEvent("Haste", "Kizant"));
    }

    private void Add(int t, GameEvent evt)
    {
        var timestamp = T0.AddSeconds(t);
        _records.Append(timestamp, evt);
        _tracker.Process(timestamp, evt);
    }

    private TimelineResult Build(QueryScope scope) =>
        TimelineBuilder.Build(_records, _tracker, "Kizant", scope);

    private static TimelineItem Item(TimelineResult result, TimelineItemKind kind, string label) =>
        result.Items.Single(i => i.Kind == kind && i.Label == label);

    [Fact]
    public void FightScopeCollectsInstantsAndClippedBuffSpans()
    {
        var fightId = _tracker.Fights.Single().Id;
        var result = Build(new QueryScope { FightIds = [fightId] });

        Assert.Equal(T0.AddSeconds(2), result.RangeBegin);
        Assert.Equal(T0.AddSeconds(8), result.RangeEnd);
        Assert.Equal(8, result.Items.Count);

        // Cast→wear-off pair fading mid-fight: span clipped at the fight start.
        var sow = Item(result, TimelineItemKind.Buff, "Spirit of Wolf");
        Assert.Equal("Kizant", sow.Actor);
        Assert.Equal(T0.AddSeconds(2), sow.Start);
        Assert.Equal(T0.AddSeconds(5), sow.End);
        Assert.True(sow.StartsBefore);
        Assert.False(sow.EndsAfter);

        // Pair whose wear-off lands after the fight: clipped at the fight end.
        var haste = Item(result, TimelineItemKind.Buff, "Haste");
        Assert.Equal(T0.AddSeconds(3), haste.Start);
        Assert.Equal(T0.AddSeconds(8), haste.End);
        Assert.False(haste.StartsBefore);
        Assert.True(haste.EndsAfter);

        // Casts inside the range are instants — including the NPC's; the
        // owner's t0 cast is outside the fight and must not appear.
        Assert.Equal(T0.AddSeconds(3), Item(result, TimelineItemKind.Cast, "Haste").Start);
        Assert.Equal("An ice giant", Item(result, TimelineItemKind.Cast, "Frost Breath").Actor);

        Assert.Equal("Raider01", Item(result, TimelineItemKind.Ability, "Bestial Fury").Actor);

        // Wear-off with no matching cast: an instant fade, not a span.
        Assert.Equal(T0.AddSeconds(6), Item(result, TimelineItemKind.Fade, "Chloroplast").Start);

        Assert.Equal("An ice giant", Item(result, TimelineItemKind.Resist, "Snare").Actor);

        var death = Item(result, TimelineItemKind.Death, "slain by Kizant");
        Assert.Equal("An ice giant", death.Actor);
        Assert.Equal(T0.AddSeconds(8), death.Start);

        Assert.All(result.Items.Where(i => i.Kind != TimelineItemKind.Buff), i => Assert.Null(i.End));
    }

    [Fact]
    public void ExplicitTimeRangeScopesTheInstants()
    {
        var result = Build(new QueryScope
        {
            TimeRanges = [new TimeRange(T0.AddSeconds(3), T0.AddSeconds(4))],
        });

        // Only t3..t4 instants; Spirit of Wolf (t0→t5) crosses the range and
        // clips on both edges.
        Assert.Contains(result.Items, i => i.Kind == TimelineItemKind.Ability);
        Assert.DoesNotContain(result.Items, i => i.Kind == TimelineItemKind.Death);
        var sow = Item(result, TimelineItemKind.Buff, "Spirit of Wolf");
        Assert.True(sow.StartsBefore);
        Assert.True(sow.EndsAfter);
        Assert.Equal(T0.AddSeconds(3), sow.Start);
        Assert.Equal(T0.AddSeconds(4), sow.End);
    }

    [Fact]
    public void EmptyScopeYieldsEmptyResult()
    {
        var result = Build(new QueryScope { FightIds = [999] });
        Assert.Null(result.RangeBegin);
        Assert.Empty(result.Items);
    }
}
