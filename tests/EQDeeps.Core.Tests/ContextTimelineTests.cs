using EQDeeps.Core.Events;
using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// Zone and level as step functions over the record stream — what the chart
/// strip draws above the plot. See <see cref="ContextTimeline"/> for why the
/// two are read from different evidence despite looking alike.
/// </summary>
public class ContextTimelineTests
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);

    private readonly RecordStore _records = new();

    private void Add(double seconds, GameEvent evt) =>
        _records.Append(T0.AddSeconds(seconds), evt);

    private static DamageEvent Swing() =>
        new("Moonchopper", "A froglok", 10, DamageKind.Melee, "Crushes");

    private ContextTimeline Build() => ContextTimeline.Build(_records, "Moonchopper");

    [Fact]
    public void AZoneHoldsUntilTheNextOne()
    {
        Add(0, new ZoneEvent("The Ruins of Old Guk"));
        Add(60, Swing());
        Add(120, new ZoneEvent("Lower Guk"));
        Add(180, Swing());

        var zones = Build().Zones;
        Assert.Equal(2, zones.Count);
        Assert.Equal("The Ruins of Old Guk", zones[0].Label);
        Assert.Equal(T0, zones[0].Range.Begin);
        Assert.Equal(T0.AddSeconds(120), zones[0].Range.End);
        Assert.Equal("Lower Guk", zones[1].Label);
        Assert.Equal(T0.AddSeconds(180), zones[1].Range.End);
    }

    /// <summary>
    /// The load screen belongs to neither zone: the transition line says the
    /// old one has already stopped being true, and the new one is not known
    /// until it names itself.
    /// </summary>
    [Fact]
    public void ATransitionClosesTheZoneWithoutOpeningOne()
    {
        Add(0, new ZoneEvent("The Ruins of Old Guk"));
        Add(60, new ZoneEvent(null));
        Add(90, new ZoneEvent("Lower Guk"));
        Add(120, Swing());

        var zones = Build().Zones;
        Assert.Equal(2, zones.Count);
        Assert.Equal(T0.AddSeconds(60), zones[0].Range.End);
        Assert.Equal(T0.AddSeconds(90), zones[1].Range.Begin);
    }

    /// <summary>
    /// The whole reason the spans are clipped to presence: the zone you logged
    /// out in is not a zone you spent the night in.
    /// </summary>
    [Fact]
    public void AZoneDoesNotSpanTheNight()
    {
        Add(0, new ZoneEvent("The Ruins of Old Guk"));
        Add(60, Swing());
        Add(9 * 3600, Swing()); // next evening, same zone, never re-announced
        Add(9 * 3600 + 60, Swing());

        var zones = Build().Zones;
        Assert.Equal(2, zones.Count);
        Assert.All(zones, z => Assert.Equal("The Ruins of Old Guk", z.Label));
        Assert.Equal(T0.AddSeconds(60), zones[0].Range.End);
        Assert.Equal(T0.AddSeconds(9 * 3600), zones[1].Range.Begin);
    }

    [Fact]
    public void ADingOpensTheNextLevel()
    {
        Add(0, new LevelEvent(41));
        Add(60, Swing());
        Add(120, new LevelEvent(42));
        Add(180, Swing());

        var levels = Build().Levels;
        Assert.Equal(2, levels.Count);
        Assert.Equal("41", levels[0].Label);
        Assert.Equal("42", levels[1].Label);
        Assert.Equal(T0.AddSeconds(120), levels[0].Range.End);
    }

    /// <summary>
    /// A ding states where the character IS. Dying can cost a level and the
    /// client says nothing, so the same number legitimately arrives twice —
    /// and the second one has to open a span, not be dismissed as a repeat of
    /// a level that ended when the player died.
    /// </summary>
    [Fact]
    public void RegainingTheSameLevelIsANewSpan()
    {
        Add(0, new LevelEvent(42));
        Add(60, new LevelEvent(43));
        Add(120, new LevelEvent(42)); // died back down, then earned it again
        Add(180, Swing());

        var levels = Build().Levels;
        Assert.Equal(3, levels.Count);
        Assert.Equal(["42", "43", "42"], levels.Select(l => l.Label));
    }

    /// <summary>A /who observes the level rather than inferring it.</summary>
    [Fact]
    public void TheOwnersWhoLineSetsTheLevel()
    {
        Add(0, new WhoEvent("Moonchopper", 31, "PAL/MNK/BER"));
        Add(60, Swing());

        var level = Assert.Single(Build().Levels);
        Assert.Equal("31", level.Label);
        Assert.Equal(T0, level.Range.Begin);
    }

    /// <summary>
    /// A /who reports a level that was already true, and a ding would have been
    /// logged had it changed on the way there — so the first one is read
    /// backwards as well as forwards. Otherwise a player who types /who in the
    /// evening has no level for the hours before it, which is most of the log.
    /// </summary>
    [Fact]
    public void TheFirstWhoIsReadBackwardsToTheStartOfTheLog()
    {
        // Gaps well under the ten minutes that would end the play session —
        // this is one evening, so the span must not be cut into pieces.
        Add(0, Swing());
        Add(300, Swing());
        Add(600, new WhoEvent("Moonchopper", 31, "PAL"));
        Add(660, Swing());

        var level = Assert.Single(Build().Levels);
        Assert.Equal(T0, level.Range.Begin);
    }

    /// <summary>
    /// A ding says the level began at that moment. Reading it backwards would
    /// claim the character was already 42 while they were earning it.
    /// </summary>
    [Fact]
    public void ADingIsNotReadBackwards()
    {
        Add(0, Swing());
        Add(300, new LevelEvent(42));
        Add(360, Swing());

        var level = Assert.Single(Build().Levels);
        Assert.Equal(T0.AddSeconds(300), level.Range.Begin);
    }

    /// <summary>A /who prints the whole zone; only one line is about you.</summary>
    [Fact]
    public void OtherPlayersWhoLinesAreIgnored()
    {
        Add(0, new WhoEvent("Razz", 50, "PAL/ENC/BER"));
        Add(1, new WhoEvent("Facestab", 25, "PAL/ROG/BER"));
        Add(60, Swing());

        Assert.Empty(Build().Levels);
    }

    /// <summary>Typing /who three times in a camp is one span, not three.</summary>
    [Fact]
    public void RepeatingTheCurrentValueIsNotAChange()
    {
        Add(0, new WhoEvent("Moonchopper", 31, "PAL"));
        Add(60, new WhoEvent("Moonchopper", 31, "PAL"));
        Add(120, new WhoEvent("Moonchopper", 31, "PAL"));
        Add(180, Swing());

        var level = Assert.Single(Build().Levels);
        Assert.Equal(T0.AddSeconds(180), level.Range.End);
    }

    [Fact]
    public void AnEmptyLogHasNoSpans()
    {
        Assert.Empty(Build().Zones);
        Assert.Empty(Build().Levels);
    }
}
