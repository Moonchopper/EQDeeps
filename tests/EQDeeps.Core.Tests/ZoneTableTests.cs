using EQDeeps.Core.Maps;
using Xunit;

namespace EQDeeps.Core.Tests;

public class ZoneTableTests
{
    private static ZoneTable Sample => ZoneTable.Parse(
        """
        # comment
        unrest	The Estate of Unrest	curated
        freportw	West Freeport	name
        freeportwest	West Freeport	curated
        gukbottom	The Ruins of Old Guk	graph

        """);

    [Fact]
    public void ResolvesADisplayNameToItsMap()
    {
        Assert.Equal(new[] { "unrest" }, Sample.MapsFor("The Estate of Unrest"));
    }

    /// <summary>
    /// The log names an instance with its difficulty attached. The geometry is
    /// the open-world zone's, so the suffix must not reach the lookup.
    /// </summary>
    [Fact]
    public void StripsTheInstanceSuffix()
    {
        Assert.Equal(new[] { "unrest" }, Sample.MapsFor("The Estate of Unrest 4 (Refined)"));
    }

    [Fact]
    public void IgnoresLeadingTheAndPunctuation()
    {
        Assert.Equal(new[] { "gukbottom" }, Sample.MapsFor("Ruins of Old Guk"));
        Assert.Equal(new[] { "gukbottom" }, Sample.MapsFor("the ruins of old guk!"));
    }

    /// <summary>
    /// A revamped zone keeps its old map beside the new one under the same
    /// display name. Both are offered; picking for the player would be a guess.
    /// </summary>
    [Fact]
    public void OffersEveryMapThatClaimsTheName()
    {
        Assert.Equal(new[] { "freportw", "freeportwest" }, Sample.MapsFor("West Freeport"));
    }

    [Fact]
    public void UnknownZoneResolvesToNothingRatherThanThrowing()
    {
        Assert.Empty(Sample.MapsFor("Somewhere That Does Not Exist"));
        Assert.Empty(Sample.MapsFor(""));
        Assert.Null(Sample.DisplayFor("nosuchmap"));
    }

    [Fact]
    public void CarriesProvenanceThrough()
    {
        Assert.Equal(ZoneNameSource.Graph, Sample.EntryFor("gukbottom")!.Source);
        Assert.Equal(ZoneNameSource.Name, Sample.EntryFor("freportw")!.Source);
        Assert.Equal(ZoneNameSource.Curated, Sample.EntryFor("unrest")!.Source);
    }

    [Fact]
    public void ShippedTableLoadsAndIsNotTrivial()
    {
        var table = ZoneTable.Default;

        Assert.True(table.Entries.Count > 200, $"Only {table.Entries.Count} zones loaded.");
        Assert.Equal(new[] { "unrest" }, table.MapsFor("The Estate of Unrest"));
        Assert.Equal("The Plane of Knowledge", table.DisplayFor("poknowledge"));
    }

    [Fact]
    public void ShippedTableHasNoDuplicateShortNames()
    {
        var duplicates = ZoneTable.Default.Entries
            .GroupBy(e => e.ShortName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, "Duplicated: " + string.Join(", ", duplicates));
    }

    /// <summary>
    /// Every display name the table claims must be one the client itself uses.
    ///
    /// <para>This is the check that keeps the hand-written rows honest: the log
    /// can only ever print a name from the client's table, so a row naming
    /// anything else is dead weight at best and a typo at worst. It caught 31
    /// of the 89 curated rows on the first pass — "Permafrost Caverns" for
    /// "Permafrost Keep", "Neriak Commons" for "Neriak - Commons".</para>
    ///
    /// <para>Opt-in: point <c>EQDEEPS_EQ</c> at an EverQuest install.</para>
    /// </summary>
    [Fact]
    public void EveryShippedDisplayNameIsOneTheClientUses()
    {
        var install = Environment.GetEnvironmentVariable("EQDEEPS_EQ");
        if (string.IsNullOrWhiteSpace(install))
        {
            return;
        }

        var path = Path.Combine(install, "Resources", "ZoneNames.txt");
        if (!File.Exists(path))
        {
            return;
        }

        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split('^');
            if (parts.Length >= 2 && parts[1].Length > 0)
            {
                known.Add(ZoneTable.Normalize(parts[1]));
            }
        }

        Assert.NotEmpty(known);

        var strangers = ZoneTable.Default.Entries
            .Where(e => !known.Contains(ZoneTable.Normalize(e.DisplayName)))
            .Select(e => $"{e.ShortName}={e.DisplayName}")
            .ToList();

        Assert.True(strangers.Count == 0, "Not client zone names: " + string.Join(", ", strangers));
    }
}
