using EQDeeps.Core.Events;
using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;
using EQDeeps.Core.Spells;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// Buff spans on the timeline, now that a buff landing is an event and the
/// spell files give a duration (F10a/F10b).
/// </summary>
public class TimelineBuffTests
{
    private static readonly DateTime T0 = new(2026, 8, 16, 20, 0, 0);

    /// <summary>A row padded out to columns 107 (formula) and 108 (cap).</summary>
    private static string Row(int id, string name, int formula, int cap) =>
        $"{id}^{name}" + new string('^', 106) + $"{formula}^{cap}";

    private static SpellBook Book() => SpellBook.Build(
        string.Join(Environment.NewLine, Row(3000, "Center", 3, 270), Row(278, "Spirit of Wolf", 3, 450)),
        string.Join(
            Environment.NewLine,
            "#SPELLINDEX^A^B^C^D^E^",
            "3000^^^You feel centered.^^^",
            "278^^^You feel the spirit of wolf enter you.^^You feel slower.^"));

    private static (RecordStore Records, FightTracker Fights) Stream(params (DateTime At, GameEvent Event)[] events)
    {
        var identity = new IdentityRegistry();
        var records = new RecordStore();
        var fights = new FightTracker(identity);
        foreach (var (at, evt) in events)
        {
            records.Append(at, evt);
        }

        return (records, fights);
    }

    private static QueryScope WholeRange => new()
    {
        TimeRanges = [new TimeRange(T0.AddMinutes(-5), T0.AddHours(2))],
    };

    [Fact]
    public void ABuffLandingOnSomeoneElseIsASpanNow()
    {
        // Nobody cast anything the log could see; the buff simply landed and
        // later faded. Before the spell files this was invisible.
        var (records, fights) = Stream(
            (T0, new LandedEvent("Raider02", "Spirit of Wolf", "You feel the spirit of wolf enter you.", 1)),
            (T0.AddMinutes(10), new WearOffEvent("Spirit of Wolf", "Raider02")));

        var result = TimelineBuilder.Build(records, fights, "Kizant", WholeRange, Book());
        var span = Assert.Single(result.Items, i => i.Kind == TimelineItemKind.Buff);
        Assert.Equal("Spirit of Wolf", span.Label);
        Assert.Equal(T0, span.Start);
        Assert.Equal(T0.AddMinutes(10), span.End);
    }

    [Fact]
    public void AnOwnCastWithNoFadeEndsWhenTheSpellSaysItWould()
    {
        // Level 23, formula 3, cap 270 → 1,620 seconds, which is exactly what
        // the owner's log measured for this spell (see SpellDurationTests).
        var (records, fights) = Stream(
            (T0.AddMinutes(-1), new LevelEvent(23)),
            (T0, new CastEvent("Kizant", "Center", CastKind.Begin)));

        var result = TimelineBuilder.Build(records, fights, "Kizant", WholeRange, Book());
        var span = Assert.Single(result.Items, i => i.Kind == TimelineItemKind.Buff);
        Assert.Equal("Center", span.Label);
        Assert.Equal(T0, span.Start);
        Assert.Equal(T0.AddSeconds(1620), span.End);
        Assert.False(span.EndsAfter);
    }

    [Fact]
    public void AFadeStillWinsOverThePrediction()
    {
        // Dispelled, zoned, or simply overwritten: what happened beats what
        // the formula expected.
        var (records, fights) = Stream(
            (T0.AddMinutes(-1), new LevelEvent(23)),
            (T0, new CastEvent("Kizant", "Center", CastKind.Begin)),
            (T0.AddMinutes(2), new WearOffEvent("Center", "Kizant")));

        var result = TimelineBuilder.Build(records, fights, "Kizant", WholeRange, Book());
        var span = Assert.Single(result.Items, i => i.Kind == TimelineItemKind.Buff);
        Assert.Equal(T0.AddMinutes(2), span.End);
    }

    [Fact]
    public void SomeoneElsesUnfadedBuffIsNotGivenAnInventedEnd()
    {
        // The duration formula needs the *caster's* level, and a buff someone
        // else cast never told us theirs. No fade, no span — better than a
        // span drawn from the owner's level by mistake.
        var (records, fights) = Stream(
            (T0.AddMinutes(-1), new LevelEvent(23)),
            (T0, new LandedEvent("Kizant", "Center", "You feel centered.", 1)));

        var result = TimelineBuilder.Build(records, fights, "Kizant", WholeRange, Book());
        Assert.DoesNotContain(result.Items, i => i.Kind == TimelineItemKind.Buff);
    }

    [Fact]
    public void WithoutTheSpellFilesNothingChanges()
    {
        var (records, fights) = Stream(
            (T0.AddMinutes(-1), new LevelEvent(23)),
            (T0, new CastEvent("Kizant", "Center", CastKind.Begin)));

        // No book: the old behaviour, which draws no span for a cast that
        // never faded.
        var result = TimelineBuilder.Build(records, fights, "Kizant", WholeRange);
        Assert.DoesNotContain(result.Items, i => i.Kind == TimelineItemKind.Buff);
    }
}
