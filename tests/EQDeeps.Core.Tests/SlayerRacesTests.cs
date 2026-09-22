using EQDeeps.Core.Achievements;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>The creature-word join table (F35, ADR-023 Decision 3) — a hand-authored TSV, embedded and loaded once.</summary>
public class SlayerRacesTests
{
    [Fact]
    public void ShippedTableLoads109DistinctTerms()
    {
        // 130 lines in slayer-races.tsv, 21 of them the header comment block (L1-21) — 109 data
        // rows. Count equalling that exact figure is also the proof that none of them collided:
        // Parse keeps the dictionary's own count, so a duplicate term overwriting an earlier one
        // would show up here as fewer than 109, not as a silent loss.
        Assert.Equal(109, SlayerRaces.Default.Count);
    }

    [Fact]
    public void SporalisJoinsToFungusman()
    {
        Assert.Equal(["Fungusman"], SlayerRaces.Default.RacesFor("Sporalis"));
    }

    [Fact]
    public void MatchingIsExactAndCaseInsensitive()
    {
        // "Clockwork Gnomeworks" is the one term in the table that carries its lead-in and joins to
        // two races (ADR-023 Decision 3) — the CWG models of Ak'Anon and Steamfont.
        Assert.Equal(["Clockwork Gnome", "Gnomework"], SlayerRaces.Default.RacesFor("CLOCKWORK GNOMEWORKS"));
        Assert.Equal(["Clockwork Gnome", "Gnomework"], SlayerRaces.Default.RacesFor("clockwork gnomeworks"));
    }

    [Fact]
    public void ATermTheTableDoesNotKnowHasNoKnownLocation()
    {
        // Shissar is a real creature-type term in the export (games has not opened those zones), and
        // is deliberately absent from the table — see the header, "a term that is NOT here has no
        // known location".
        Assert.Empty(SlayerRaces.Default.RacesFor("Shissar"));
        Assert.Empty(SlayerRaces.Default.RacesFor("Something Nobody Ever Wrote"));
    }

    [Fact]
    public void ParseSkipsCommentsAndBlanksAndNeverThrows()
    {
        var table = SlayerRaces.Parse(
            """
            # a comment line, and a blank line follows

            Kobolds	Kobold
            NoRaceHere		explained here
            	Orc	no term, skipped
            Orcs	Orc|Orc Pawn	two races
            """);

        Assert.Equal(2, table.Count);
        Assert.Equal(["Kobold"], table.RacesFor("Kobolds"));
        Assert.Equal(["Orc", "Orc Pawn"], table.RacesFor("Orcs"));
        Assert.Empty(table.RacesFor("NoRaceHere"));

        // Junk that never resembles a row at all — control bytes, a row of nothing but tabs, a
        // single column with no separator — must not throw either, and adds nothing.
        var junk = SlayerRaces.Parse("\x01\x02\x03\n\t\t\t\njust one column\n");
        Assert.Equal(0, junk.Count);
        Assert.Empty(junk.RacesFor("anything"));
    }
}
