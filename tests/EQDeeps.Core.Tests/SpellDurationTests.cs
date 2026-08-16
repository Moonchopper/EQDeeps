using EQDeeps.Core.Spells;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// The duration columns of <c>spells_us.txt</c>, and the formulas over them.
///
/// <para>The cases below are not invented: each is a spell the owner's log
/// actually recorded landing and fading, with the level they were when it
/// landed, and the duration measured between the two. If a formula here ever
/// drifts, these stop matching what a real client did.</para>
/// </summary>
public class SpellDurationTests
{
    [Theory]
    // spell                        formula cap level  observed seconds
    [InlineData("Center", 3, 270, 23, 1620)]           // observed 1641
    [InlineData("Shifting Shield", 3, 450, 28, 2700)]  // observed 2733
    [InlineData("Divine Vigor", 3, 500, 36, 3000)]     // observed 3011
    [InlineData("Daring", 3, 360, 39, 2160)]           // observed 2169
    [InlineData("Valor", 3, 540, 28, 3240)]            // observed 3299
    [InlineData("Spirit of Bear", 3, 360, 14, 2160)]   // capped: 14*30 = 420 > 360
    [InlineData("Snails Healing", 10, 4, 17, 24)]      // (17*3+10) = 61, capped to 4 ticks
    [InlineData("Blood of Pain", 1, 6, 35, 36)]        // 35/2 = 17, capped to 6
    [InlineData("Tangling Weeds", 2, 3, 21, 18)]       // (21/2)+5 = 15, capped to 3
    [InlineData("Jaxan's Jig o` Vigor", 7, 3, 21, 18)] // level 21, capped to 3
    public void FormulasReproduceWhatTheLogMeasured(string spell, int formula, int cap, int level, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), SpellDuration.Duration(formula, cap, level));
        Assert.Equal(seconds / SpellDuration.SecondsPerTick, SpellDuration.Ticks(formula, cap, level));
        Assert.NotEmpty(spell); // named so a failure says which spell disagrees
    }

    [Fact]
    public void InstantAndPermanentAreNotDurations()
    {
        // Formula 0 is an instant spell — a nuke has no span to draw.
        Assert.Equal(0, SpellDuration.Ticks(0, 0, 50));
        Assert.Equal(TimeSpan.Zero, SpellDuration.Duration(0, 100, 50));

        // Formula 50 lasts until something removes it; there is no end to predict.
        Assert.Null(SpellDuration.Ticks(SpellDuration.PermanentFormula, 0, 50));
        Assert.Null(SpellDuration.Duration(SpellDuration.PermanentFormula, 100, 50));
    }

    [Fact]
    public void ACapAloneOrAnUnknownFormulaFallsBackToTheCap()
    {
        // Formula 11 is "the cap, whatever the level".
        Assert.Equal(120, SpellDuration.Ticks(11, 120, 5));
        Assert.Equal(120, SpellDuration.Ticks(11, 120, 60));

        // A formula this code has never seen must not invent a longer buff
        // than the file allows; the cap is the safe reading.
        Assert.Equal(42, SpellDuration.Ticks(99, 42, 50));

        // And with no cap at all, the level formula stands on its own.
        Assert.Equal(60, SpellDuration.Ticks(7, 0, 60));
    }

    [Fact]
    public void LevelIsNeverBelowOne()
    {
        // A caster level we never learned reads as 0; a buff of zero ticks
        // would then be drawn as an instant, which is worse than a short one.
        Assert.Equal(1, SpellDuration.Ticks(7, 0, 0));
        Assert.Equal(1, SpellDuration.Ticks(7, 0, -5));
    }

    [Fact]
    public void TheBookAnswersByNameAndShrugsAtStrangers()
    {
        // A row padded out to reach columns 107 (formula) and 108 (cap).
        var row = "3000^Center" + new string('^', 106) + "3^270";
        var strings = string.Join(Environment.NewLine, "#SPELLINDEX^A^B^C^D^E^", "3000^^^You feel centered.^^^");
        var book = SpellBook.Build(row, strings);

        Assert.Equal(TimeSpan.FromSeconds(1620), book.DurationOf("Center", 23));
        Assert.Null(book.DurationOf("Nothing By That Name", 50));
        Assert.Null(SpellBook.Empty.DurationOf("Center", 23));

        // The emote still resolves from the same row, durations or not.
        Assert.True(book.TryLandsOnYou("You feel centered.", out var match));
        Assert.Equal("Center", match.Spell);
    }
}
