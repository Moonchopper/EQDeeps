using EQDeeps.Core.Achievements;
using Xunit;

namespace EQDeeps.Core.Tests;

public class SlayerTests
{
    // See AchievementExportTests.Line — same reasoning, kept local to this file per house style
    // (the test folder is flat, and each file is self-contained).
    private static string Line(params string[] columns) => string.Join('\t', columns);

    [Fact]
    public void MetaReferencesResolveExactlyOnceNormalisedOrNotAtAll()
    {
        var text = string.Join('\n',
        [
            "Slayer: Conquest",
            Line("I", "SomeExactTitle"),
            Line("I", "", "Widgets", "1/2"),
            Line("I", "Catnipped In the Bud"),
            Line("I", "", "Gnolls", "1/2"),

            "Slayer: General",
            Line("I", "Meta Example"),
            Line("C", "", "Complete the achievement \"SomeExactTitle\""),
            Line("I", "", "Complete the achievement \"Catnipped in the bud.\""),
            Line("I", "", "Complete the achievement \"Nothing Matches This\""),
        ]);

        var file = AchievementExport.Parse(text);
        Assert.Equal(0, file.SkippedLines);

        var progress = Slayer.From(file);

        var meta = Assert.Single(progress.Meta);
        Assert.Equal(3, meta.Components.Count);

        var exact = meta.Components[0];
        Assert.Equal("SomeExactTitle", exact.Title);
        // Written C on the reference line even though the target achievement above is I — the
        // reference keeps its own state, never the target's (ADR-023 Decision 2).
        Assert.True(exact.Complete);
        Assert.Equal("Slayer: Conquest/SomeExactTitle", exact.TargetKey);

        var normalised = meta.Components[1];
        // Title is the quoted text as written — including the trailing period the export used,
        // even though the achievement it resolves to spells it without one.
        Assert.Equal("Catnipped in the bud.", normalised.Title);
        Assert.False(normalised.Complete);
        Assert.Equal("Slayer: Conquest/Catnipped In the Bud", normalised.TargetKey);

        var unmatched = meta.Components[2];
        Assert.Equal("Nothing Matches This", unmatched.Title);
        Assert.False(unmatched.Complete);
        Assert.Null(unmatched.TargetKey);
    }

    [Fact]
    public void DuplicateCategoryAndTitleGetDistinctKeys()
    {
        var text = string.Join('\n',
        [
            "Slayer: Conquest",
            Line("I", "Pesticide"),
            Line("I", "", "Roaches", "1/2"),
            Line("I", "Pesticide"),
            Line("I", "", "Spiders", "3/4"),
        ]);

        var file = AchievementExport.Parse(text);
        var progress = Slayer.From(file);

        Assert.Equal(2, progress.Kills.Count);
        Assert.Equal("Slayer: Conquest/Pesticide", progress.Kills[0].Key);
        Assert.Equal("Slayer: Conquest/Pesticide#2", progress.Kills[1].Key);
    }

    [Fact]
    public void FractionAveragesNonOptionalComponentsAndRemainingSumsWhatIsLeft()
    {
        var text = string.Join('\n',
        [
            "Slayer: Skill",
            Line("I", "Fraction Test"),
            Line("C", "", "Done Thing"),
            Line("I", "", "Third Done", "3/10"),
            Line("I", "", "Zero Done", "0/10"),
            Line("I", "", "(Optional) Ignored", "0/10"),
        ]);

        var file = AchievementExport.Parse(text);
        var progress = Slayer.From(file);

        var achievement = Assert.Single(progress.Kills);
        Assert.Equal("Skill", achievement.Tier);
        // (1 + 0.3 + 0) / 3 — the optional 0/10 component is excluded from both the mean and the
        // denominator, per ADR-023: only non-optional components count toward progress.
        Assert.Equal((1.0 + 0.3 + 0.0) / 3.0, achievement.Fraction, precision: 10);
        // (10-3) + (10-0) = 17 — the optional component's 10 needed is not owed.
        Assert.Equal(17, achievement.Remaining);
    }

    [Fact]
    public void AnAchievementTheFileMarksCompleteHasFractionOne()
    {
        var text = string.Join('\n',
        [
            "Slayer: Skill",
            Line("C", "Already Done"),
            Line("C", "", "Some Thing", "0/100"), // component still looks unfinished on paper
        ]);

        var file = AchievementExport.Parse(text);
        var progress = Slayer.From(file);

        var achievement = Assert.Single(progress.Kills);
        Assert.True(achievement.Complete);
        // The export's own "done" outranks a recomputation from a component that, per the domain
        // doc, would not even carry a count once the parent is finished — but this file is
        // deliberately hostile about it, and Fraction must not choke on the disagreement.
        Assert.Equal(1.0, achievement.Fraction, precision: 10);
        // The one component is written C, so it is not "open" — nothing qualifies for Remaining,
        // which is null rather than 0 (there is no open count to sum, not a sum of zero).
        Assert.Null(achievement.Remaining);
    }

    [Fact]
    public void NonSlayerCategoryNeverAppearsInSlayerProgress()
    {
        var text = string.Join('\n',
        [
            "Untapped Potential: Races",
            Line("I", "Racial Diversity"),
            Line("I", "", "Barbarians", "1/15"),

            "Slayer: Conquest",
            Line("I", "Pesticide"),
            Line("I", "", "Roaches", "1/2"),
        ]);

        var file = AchievementExport.Parse(text);
        Assert.Equal(2, file.Achievements.Count);
        Assert.Contains(file.Achievements, a => a.Category == "Untapped Potential: Races");

        var progress = Slayer.From(file);

        Assert.DoesNotContain(progress.Meta, m => m.Title == "Racial Diversity");
        Assert.DoesNotContain(progress.Kills, k => k.Title == "Racial Diversity");
        var kill = Assert.Single(progress.Kills);
        Assert.Equal("Pesticide", kill.Title);
        Assert.Empty(progress.Meta);
    }

    [Fact]
    public void ASingleTermIsOneTerm()
    {
        Assert.Equal(["Kobolds"], Slayer.TermsOf("Kobolds"));
    }

    [Fact]
    public void CommaAndTrailingOxfordAndSplitIntoThreeTerms()
    {
        Assert.Equal(
            ["Alligators", "Basilisks", "Crocodiles"],
            Slayer.TermsOf("Alligators, Basilisks, and Crocodiles."));
    }

    [Fact]
    public void PlainAndSplitsIntoTwoTerms()
    {
        Assert.Equal(["Orcs", "Wereorcs"], Slayer.TermsOf("Orcs and Wereorcs."));
    }

    [Fact]
    public void APhraseWithNoDelimiterIsOneTerm()
    {
        Assert.Equal(["The playable races"], Slayer.TermsOf("The playable races."));
    }

    [Fact]
    public void AndInsideAPhraseSplitsOnlyOnce()
    {
        Assert.Equal(
            ["Iksars", "Kylong Iksars of Veksar"],
            Slayer.TermsOf("Iksars and Kylong Iksars of Veksar."));
    }

    /// <summary>
    /// The real Clockwork Conquest/Special/Skill component text (the owner's export, all three
    /// tiers share it verbatim): a lead-in that must be prefixed to every one of the nine terms it
    /// introduces, with an Oxford ", and" before the last one. Only "Clockwork Gnomeworks" has
    /// anywhere to go in slayer-races.tsv — the other eight terms this produces are real Slayer
    /// terms with no known location in this game, per ADR-023 Decision 3 — but TermsOf's job is the
    /// split, not the join, so all nine come back regardless.
    /// </summary>
    [Fact]
    public void TheLeadInIsStrippedAndPrefixedToEveryTermAfterIt()
    {
        var terms = Slayer.TermsOf(
            "Clockwork: Beetles, Boars, Dragons, Rats, Snakes, Spiders, Gnomeworks, Copters, and Tin Soldiers.");

        Assert.Equal(
            [
                "Clockwork Beetles", "Clockwork Boars", "Clockwork Dragons", "Clockwork Rats",
                "Clockwork Snakes", "Clockwork Spiders", "Clockwork Gnomeworks", "Clockwork Copters",
                "Clockwork Tin Soldiers",
            ],
            terms);
    }
}
