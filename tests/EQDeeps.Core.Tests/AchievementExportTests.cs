using EQDeeps.Core.Achievements;
using Xunit;

namespace EQDeeps.Core.Tests;

public class AchievementExportTests
{
    // Joins tab-separated columns so no literal tab is ever typed into the source — an editor
    // silently turning a tab into spaces would break a fixture with no visible sign (brief's own
    // warning, worth repeating: this is why the real export never becomes a fixture file either).
    private static string Line(params string[] columns) => string.Join('\t', columns);

    [Fact]
    public void FourLineShapesParse()
    {
        var text = string.Join('\n',
        [
            "Slayer: Conquest",
            Line("I", "Puttin' On The Dog"),
            Line("I", "", "Kobolds", "570/5000"),
            Line("C", "", "Skeletons"),
        ]);

        var file = AchievementExport.Parse(text);

        Assert.Equal(0, file.SkippedLines);
        var achievement = Assert.Single(file.Achievements);
        Assert.Equal("Slayer: Conquest", achievement.Category);
        Assert.Equal("Puttin' On The Dog", achievement.Title);
        Assert.False(achievement.Complete);
        Assert.Equal(2, achievement.Components.Count);

        var counted = achievement.Components[0];
        Assert.Equal("Kobolds", counted.Text);
        Assert.False(counted.Complete);
        Assert.False(counted.Optional);
        Assert.Equal(570, counted.Have);
        Assert.Equal(5000, counted.Need);

        var finished = achievement.Components[1];
        Assert.Equal("Skeletons", finished.Text);
        Assert.True(finished.Complete);
        Assert.False(finished.Optional);
        Assert.Null(finished.Have);
        Assert.Null(finished.Need);
    }

    [Fact]
    public void OptionalPrefixIsStrippedAndFlagged()
    {
        var text = string.Join('\n',
        [
            "Slayer: General",
            Line("I", "Some Achievement"),
            Line("I", "", "(Optional) Something", "0/5"),
        ]);

        var file = AchievementExport.Parse(text);

        var component = Assert.Single(Assert.Single(file.Achievements).Components);
        Assert.Equal("Something", component.Text);
        Assert.True(component.Optional);
        Assert.Equal(0, component.Have);
        Assert.Equal(5, component.Need);
    }

    [Fact]
    public void CrlfAndLfParseIdentically()
    {
        string[] lines =
        [
            "Slayer: Conquest",
            Line("I", "Puttin' On The Dog"),
            Line("I", "", "Kobolds", "570/5000"),
            Line("C", "", "Skeletons"),
            "Slayer: Special",
            Line("C", "Pesticide"),
            Line("C", "", "Roaches"),
        ];

        var crlf = AchievementExport.Parse(string.Join("\r\n", lines));
        var lf = AchievementExport.Parse(string.Join("\n", lines));

        AssertFilesEqual(crlf, lf);
    }

    // Achievement and AchievementExportFile each carry a nested IReadOnlyList<T> field, and the
    // concrete List<T> behind it has no structural Equals — two separately-parsed records with
    // identical content are NOT record-equal by reference. Comparing scalar fields directly, and
    // handing each Components list to Assert.Equal as its own top-level call (its element type,
    // AchievementComponent, is all-scalar, so that call is a genuine structural check), is what
    // "the results are equal" actually needs to mean here.
    private static void AssertFilesEqual(AchievementExportFile expected, AchievementExportFile actual)
    {
        Assert.Equal(expected.SkippedLines, actual.SkippedLines);
        Assert.Equal(expected.Achievements.Count, actual.Achievements.Count);
        for (var i = 0; i < expected.Achievements.Count; i++)
        {
            var e = expected.Achievements[i];
            var a = actual.Achievements[i];
            Assert.Equal(e.Category, a.Category);
            Assert.Equal(e.Title, a.Title);
            Assert.Equal(e.Complete, a.Complete);
            Assert.Equal(e.Components, a.Components);
        }
    }

    [Fact]
    public void HostileInputNeverThrowsAndCountsWhatItSkips()
    {
        var overlong = new string('x', 5000);
        var junk = "\x01\x02\t\x03\x04"; // has a tab, but its first token is not "I" or "C"
        var text = string.Join('\n',
        [
            "Slayer: Conquest",
            Line("I", "", "Orphan Component", "1/2"), // no achievement line precedes it
            overlong,
            junk,
            Line("I", "Real Achievement"),
            Line("I", "", "Real Component", "1/2"),
        ]);

        var file = AchievementExport.Parse(text);

        // 3 skipped: the orphan component (no open achievement), the 5,000-char line (over the
        // 1,024 limit), and the junk line (a tab present, but not an "I"/"C" state). The real
        // achievement and its real component still parse.
        Assert.Equal(3, file.SkippedLines);
        var achievement = Assert.Single(file.Achievements);
        Assert.Equal("Real Achievement", achievement.Title);
        Assert.Single(achievement.Components);

        var empty = AchievementExport.Parse("");
        Assert.Empty(empty.Achievements);
        Assert.Equal(0, empty.SkippedLines);

        var oversized = AchievementExport.Parse(new string('a', 4 * 1024 * 1024 + 1));
        Assert.Empty(oversized.Achievements);
        Assert.Equal(1, oversized.SkippedLines);
    }

    [Fact]
    public void PathForMatchesInstallNamingConvention()
    {
        Assert.Equal(Path.Combine(@"C:\EQ", "Moonchopper_qeynos-Achievements.txt"),
            AchievementExport.PathFor(@"C:\EQ", "Moonchopper", "qeynos"));
    }
}
