using EQDeeps.Core.Achievements;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// The factions the export itself says this player is working on (F35, ADR-023 Decision 5) —
/// every "Get maximum faction with X." component, in any category, not only Slayer's.
/// </summary>
public class ProtectedFactionsTests
{
    private static string Line(params string[] columns) => string.Join('\t', columns);

    [Fact]
    public void OpenVersusEarnedAndATwoUnlockFaction()
    {
        var text = string.Join('\n',
        [
            "Untapped Potential: Races",
            Line("I", "Racial Diversity"),
            Line("I", "", "Get maximum faction with Wolves of the North."),
            Line("C", "", "Get maximum faction with Kelethin."),

            "Untapped Potential: Classes",
            Line("I", "Class Unlocks"),
            Line("I", "", "Get maximum faction with Wolves of the North."),
        ]);

        var file = AchievementExport.Parse(text);
        Assert.Equal(0, file.SkippedLines);

        var protectedFactions = ProtectedFactions.From(file);

        Assert.Equal(2, protectedFactions.Count);

        // Named by two unlocks, one still open — every component naming it must be complete for
        // UnlockEarned to be true, and here one of the two is not.
        var wolves = protectedFactions[0];
        Assert.Equal("Wolves of the North", wolves.Name);
        Assert.False(wolves.UnlockEarned);
        Assert.Equal(["Racial Diversity", "Class Unlocks"], wolves.Unlocks);

        // Named once, and that one component is complete.
        var kelethin = protectedFactions[1];
        Assert.Equal("Kelethin", kelethin.Name);
        Assert.True(kelethin.UnlockEarned);
        Assert.Equal(["Racial Diversity"], kelethin.Unlocks);

        Assert.Equal(FactionNames.Key("Wolves of the North"), wolves.Key);
        Assert.Equal(FactionNames.Key("Kelethin"), kelethin.Key);
    }

    [Fact]
    public void OnlyMatchingComponentsAreRead()
    {
        var text = string.Join('\n',
        [
            "Slayer: Conquest",
            Line("I", "Pesticide"),
            Line("I", "", "Roaches", "1/2"),

            "Untapped Potential: Deity",
            Line("C", "Deity Devotion"),
            Line("C", "", "Get maximum faction with Brell Serilis."),
        ]);

        var file = AchievementExport.Parse(text);
        var protectedFactions = ProtectedFactions.From(file);

        var only = Assert.Single(protectedFactions);
        Assert.Equal("Brell Serilis", only.Name);
        Assert.True(only.UnlockEarned);
    }

    [Fact]
    public void NoUnlockComponentsAnywhereYieldsNoProtectedFactions()
    {
        var text = string.Join('\n', ["Slayer: Conquest", Line("I", "Pesticide"), Line("I", "", "Roaches", "1/2")]);
        var file = AchievementExport.Parse(text);

        Assert.Empty(ProtectedFactions.From(file));
    }
}
