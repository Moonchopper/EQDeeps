using EQDeeps.Core.Raids;
using EQDeeps.Core.Reference;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// C1: the roster loader. Same tolerance idiom as
/// <see cref="Maps.ZoneTable.Parse"/> (skip, never throw) plus two traps
/// specific to this file's shape (F31-3 brief Recon §1, gotchas A/B).
/// </summary>
public class RaidTargetsTests
{
    private static RaidTargets Sample => RaidTargets.Parse(
        "# comment\n" +
        "name\taliases\tzone\tgroup\n" +
        "Lord Nagafen\t\tNagafen's Lair\tDragons\n" +
        "Cazic-Thule\tCazic Thule\tThe Plane of Fear\tPlane of Fear\n" +
        "Multi Alias Target\tAlias One | Alias Two\tSomewhere\tGroup Two\n" +
        "\n" +
        "# another comment, and a row with too few cells below\n" +
        "Missing Group\t\tSomewhere\n" +
        "Empty Zone\t\t\tGroup Two\n" +
        "Third Target\t\tElsewhere\tGroup Two\n");

    [Fact]
    public void ReadsNameZoneAndGroup()
    {
        Assert.Equal("Lord Nagafen", Sample.Targets[0].Name);
        Assert.Equal("Nagafen's Lair", Sample.Targets[0].Zone);
        Assert.Equal("Dragons", Sample.Targets[0].Group);
    }

    /// <summary>Gotcha A: the header row is recognised by its first cell, not skipped positionally.</summary>
    [Fact]
    public void IgnoresTheHeaderRowByItsFirstCellRatherThanByPosition()
    {
        Assert.DoesNotContain(Sample.Targets, t => t.Name == "name");
    }

    [Fact]
    public void SkipsCommentsAndBlankLines()
    {
        Assert.DoesNotContain(Sample.Targets, t => t.Name.StartsWith('#'));
        Assert.Equal(4, Sample.Targets.Count); // comments, blanks and the two malformed rows below all excluded
    }

    /// <summary>A row with too few cells (no group column at all) is skipped, never thrown.</summary>
    [Fact]
    public void SkipsARowWithTooFewCells()
    {
        Assert.DoesNotContain(Sample.Targets, t => t.Name == "Missing Group");
    }

    /// <summary>A row with all four cells but an empty required one (zone) is skipped.</summary>
    [Fact]
    public void SkipsARowWithAnEmptyRequiredCell()
    {
        Assert.DoesNotContain(Sample.Targets, t => t.Name == "Empty Zone");
    }

    /// <summary>Gotcha B: an empty aliases cell (two consecutive tabs) is an empty list, never [""].</summary>
    [Fact]
    public void EmptyAliasesCellProducesAnEmptyListNeverASingleEmptyString()
    {
        Assert.Empty(Sample.Targets[0].Aliases);
    }

    [Fact]
    public void ReadsASingleAlias()
    {
        Assert.Equal(new[] { "Cazic Thule" }, Sample.Targets[1].Aliases);
    }

    [Fact]
    public void ReadsSeveralPipeSeparatedAliasesTrimmed()
    {
        Assert.Equal(new[] { "Alias One", "Alias Two" }, Sample.Targets[2].Aliases);
    }

    [Fact]
    public void PreservesFileOrder()
    {
        Assert.Equal(
            new[] { "Lord Nagafen", "Cazic-Thule", "Multi Alias Target", "Third Target" },
            Sample.Targets.Select(t => t.Name));
    }

    /// <summary>The shipped file (copied verbatim from the architect's draft) loads and is not empty.</summary>
    [Fact]
    public void TheShippedFileLoadsInFileOrder()
    {
        Assert.NotEmpty(RaidTargets.Default.Targets);
        Assert.Equal("Lord Nagafen", RaidTargets.Default.Targets[0].Name);
        Assert.DoesNotContain(RaidTargets.Default.Targets, t => t.Name == "name");
    }
}

/// <summary>
/// C2: the shared match key. A logged name meets a roster name (or one of its
/// aliases) under <see cref="NpcIndex.Normalize"/> plus
/// <see cref="StringComparer.OrdinalIgnoreCase"/> — the exact mechanism
/// <see cref="NpcIndex"/> already uses (its case-insensitivity comes from the
/// comparer its own dictionary is built with, not from <c>Normalize</c>
/// alone) and <c>mobKey</c> mirrors on the UI side. This is a property of
/// that shared mechanism, not of anything new in this feature — proven here
/// against the four named cases (F31-3 brief Recon §2) so a future change to
/// either half cannot silently break the join.
/// </summary>
public class RaidTargetMatchKeyTests
{
    private static bool Matches(string logged, string rosterField) =>
        string.Equals(NpcIndex.Normalize(logged), NpcIndex.Normalize(rosterField), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void ClericOfInnoruukNeverMatchesInnoruuk()
    {
        // "Innoruuk" appears in most kills of the Cleric, who is not the god —
        // whole-name matching is what keeps them apart.
        Assert.False(Matches("Cleric of Innoruuk", "Innoruuk"));
    }

    [Fact]
    public void AnArticledLoggedNameMatchesTheBareRosterName()
    {
        Assert.True(Matches("a thunder spirit princess", "Thunder Spirit Princess"));
    }

    [Fact]
    public void TheRosterNameMatchesItself()
    {
        Assert.True(Matches("Cazic-Thule", "Cazic-Thule"));
    }

    /// <summary>
    /// The hyphen is not normalised away by the key on either side — the
    /// alias column ("Cazic Thule", no hyphen) is what bridges the logged
    /// space-separated spelling to the roster's hyphenated name.
    /// </summary>
    [Fact]
    public void TheHyphenIsNotNormalisedAwayTheAliasBridgesIt()
    {
        Assert.False(Matches("Cazic Thule", "Cazic-Thule")); // the NAME, no match
        Assert.True(Matches("Cazic Thule", "Cazic Thule")); // the ALIAS, matches
    }

    [Fact]
    public void ABareLoggedNameMatchesTheAliasOfATitledRosterName()
    {
        Assert.True(Matches("Innoruuk", "Innoruuk")); // matched against the alias field, not the titled name
        Assert.False(Matches("Innoruuk", "Innoruuk, the Prince of Hate"));
    }
}
