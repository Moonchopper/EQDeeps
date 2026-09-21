using EQDeeps.Core.Achievements;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary><c>/outputfile faction</c> (F35, ADR-023 Decision 5) — the second export the planner reads, per class loadout.</summary>
public class FactionExportTests
{
    private static string Line(params string[] columns) => string.Join('\t', columns);

    [Fact]
    public void HeaderIsSkippedUncountedAndRowsParse()
    {
        var text = string.Join('\n',
        [
            Line("ID", "Name", "StandingValue", "PointsToMax"),
            Line("65", "Brownies of Faydwer", "0", "2000"),
            Line("217", "Allize Volew", "570", "1430"),
        ]);

        var file = FactionExport.Parse(text);

        Assert.Equal(0, file.SkippedLines);
        Assert.Equal(2, file.Factions.Count);
        Assert.Equal(new FactionStanding(65, "Brownies of Faydwer", 0), file.Factions[0]);
        Assert.Equal(new FactionStanding(217, "Allize Volew", 570), file.Factions[1]);
    }

    [Fact]
    public void NegativeStandingParses()
    {
        var file = FactionExport.Parse(Line("218", "Allize Taeew", "-2000", "4000"));

        var row = Assert.Single(file.Factions);
        Assert.Equal(-2000, row.Standing);
    }

    [Fact]
    public void AThreeColumnRowIsAccepted()
    {
        // PointsToMax is ignored entirely — always 2000 - StandingValue, so a row that omits it
        // still carries everything Parse reads.
        var file = FactionExport.Parse(Line("223", "Circle of Unseen Hands", "2000"));

        var row = Assert.Single(file.Factions);
        Assert.Equal(new FactionStanding(223, "Circle of Unseen Hands", 2000), row);
    }

    [Fact]
    public void CrlfAndLfParseIdentically()
    {
        var lines = string.Join('\n',
        [
            Line("ID", "Name", "StandingValue", "PointsToMax"),
            Line("65", "Brownies of Faydwer", "0", "2000"),
            Line("217", "Allize Volew", "570", "1430"),
        ]);

        var crlf = FactionExport.Parse(lines.Replace("\n", "\r\n"));
        var lf = FactionExport.Parse(lines);

        Assert.Equal(lf.SkippedLines, crlf.SkippedLines);
        Assert.Equal(lf.Factions, crlf.Factions);
    }

    [Fact]
    public void HostileInputNeverThrowsAndCountsExactlyWhatItSkips()
    {
        var overlong = new string('x', 5000);
        var text = string.Join('\n',
        [
            Line("ID", "Name", "StandingValue", "PointsToMax"), // uncounted
            Line("65", "Brownies of Faydwer", "0", "2000"),     // good
            Line("not an id", "Junk Name", "0"),                // bad id
            Line("70", "No Standing Here"),                     // too few columns
            Line("71", "", "500"),                               // empty name
            Line("72", "Bad Standing", "not a number"),          // bad standing
            overlong,                                            // over the 1,024-char limit
            Line("217", "Allize Volew", "570", "1430"),          // good
        ]);

        var file = FactionExport.Parse(text);

        // 5 skipped: bad id, too few columns, empty name, bad standing, overlong. The header is
        // recognised and does not count, and the two good rows parse.
        Assert.Equal(5, file.SkippedLines);
        Assert.Equal(2, file.Factions.Count);

        var empty = FactionExport.Parse("");
        Assert.Empty(empty.Factions);
        Assert.Equal(0, empty.SkippedLines);

        var oversized = FactionExport.Parse(new string('a', 4 * 1024 * 1024 + 1));
        Assert.Empty(oversized.Factions);
        Assert.Equal(1, oversized.SkippedLines);
    }

    [Fact]
    public void SearchPatternMatchesEveryClassLoadout()
    {
        Assert.Equal("Moonchopper_qeynos-*Factions.txt", FactionExport.SearchPattern("Moonchopper", "qeynos"));
    }

    [Fact]
    public void ClassOfReadsTheSegmentBetweenThePrefixAndTheSuffix()
    {
        Assert.Equal("SHD", FactionExport.ClassOf("Moonchopper_qeynos-SHD-Factions.txt", "Moonchopper", "qeynos"));
        Assert.Null(FactionExport.ClassOf("Moonchopper_qeynos-Factions.txt", "Moonchopper", "qeynos"));
        Assert.Null(FactionExport.ClassOf("SomeoneElse_qeynos-SHD-Factions.txt", "Moonchopper", "qeynos"));
        Assert.Null(FactionExport.ClassOf("short.txt", "Moonchopper", "qeynos"));
    }
}
