using EQDeeps.Core.Maps;
using Xunit;

namespace EQDeeps.Core.Tests;

public class ZoneTableTests
{
    private static ZoneTable Sample => ZoneTable.Parse(
        """
        # comment
        unrest	The Estate of Unrest	curated	classic	id	63
        freportw	West Freeport	name
        freeportwest	West Freeport	curated	classic	id	9,383
        gukbottom	The Ruins of Old Guk	graph	classic	curated
        poknowledge	The Plane of Knowledge	curated	pop	id	202
        newsebexp	New Sebilis Expedition	curated			99
        oddity	Halas	curated	atlantis	id	29,x,29

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

    /// <summary>
    /// The era columns are optional and carry their own provenance. A code this
    /// build does not know reads as no era rather than failing the row: an
    /// unplaced zone is shown under every filter, so that is the safe reading.
    /// </summary>
    [Fact]
    public void ReadsTheEraColumnsAndTheirProvenance()
    {
        Assert.Equal("classic", Sample.EraFor("unrest"));
        Assert.Equal(ZoneEraSource.Id, Sample.EntryFor("unrest")!.EraSource);
        Assert.Equal(ZoneEraSource.Curated, Sample.EntryFor("gukbottom")!.EraSource);
        Assert.Equal("pop", Sample.EraFor("poknowledge"));

        // Three columns: the row says nothing about eras.
        Assert.Null(Sample.EraFor("freportw"));
        Assert.Null(Sample.EntryFor("freportw")!.EraSource);
        Assert.Null(Sample.EraFor("newsebexp"));

        // An era this build does not know is no era, and no source either.
        Assert.Null(Sample.EraFor("oddity"));
        Assert.Null(Sample.EntryFor("oddity")!.EraSource);

        Assert.Null(Sample.EraFor("nosuchmap"));
    }

    /// <summary>
    /// The ids column is the sixth, so an id-only row carries blank era cells;
    /// a name with several ids keeps them all in order; two drawings of one
    /// name share its ids and both come back for it. A cell that is not a
    /// number is dropped, and a repeat is folded, without losing the row.
    /// </summary>
    [Fact]
    public void ReadsTheIdsColumnAndLooksUpByIt()
    {
        Assert.Equal(new[] { 63 }, Sample.EntryFor("unrest")!.Ids);
        Assert.Equal(new[] { 9, 383 }, Sample.EntryFor("freeportwest")!.Ids);
        Assert.Empty(Sample.EntryFor("freportw")!.Ids);

        Assert.Equal(new[] { 99 }, Sample.EntryFor("newsebexp")!.Ids);
        Assert.Null(Sample.EraFor("newsebexp"));

        Assert.Equal(new[] { 29 }, Sample.EntryFor("oddity")!.Ids);

        Assert.Equal(new[] { "unrest" }, Sample.ZonesForId(63).Select(e => e.ShortName));
        Assert.Equal(new[] { "freeportwest" }, Sample.ZonesForId(383).Select(e => e.ShortName));
        Assert.Empty(Sample.ZonesForId(12345));
    }

    /// <summary>
    /// Every shipped row carries the client's ids for its name — the Bestiary
    /// (F30) addresses a zone's roster by them — and the spot checks are the
    /// joins ADR-020 was verified against.
    /// </summary>
    [Fact]
    public void ShippedTableCarriesZoneIds()
    {
        var table = ZoneTable.Default;

        Assert.All(table.Entries, e => Assert.NotEmpty(e.Ids));
        Assert.Equal(new[] { 42 }, table.EntryFor("neriakc")!.Ids);
        Assert.Equal(new[] { 12 }, table.EntryFor("qey2hh1")!.Ids);
        Assert.Equal(new[] { 58 }, table.EntryFor("crushbone")!.Ids);
        Assert.Contains(9, table.EntryFor("freportw")!.Ids);
        Assert.Contains("freportw", table.ZonesForId(9).Select(e => e.ShortName));
        Assert.Contains("freeportwest", table.ZonesForId(9).Select(e => e.ShortName));
    }

    /// <summary>
    /// The shipped eras were derived by <c>scripts/derive-zone-eras.mjs</c>
    /// and are pinned here in outline: every era names a real expansion,
    /// carries a source, and the spot checks are the cases the derivation
    /// argues about — a launch zone filed in the Kunark id block, a place
    /// whose revamps keep its name, and a Legends-only zone left unplaced.
    /// </summary>
    [Fact]
    public void ShippedTableErasAreKnownAndSourced()
    {
        var table = ZoneTable.Default;

        var withEra = table.Entries.Where(e => e.Era is not null).ToList();
        Assert.True(withEra.Count > 250, $"Only {withEra.Count} rows carry an era.");
        Assert.All(withEra, e => Assert.True(ZoneEras.IsKnown(e.Era), $"{e.ShortName}: unknown era {e.Era}"));
        Assert.All(withEra, e => Assert.NotNull(e.EraSource));
        Assert.All(table.Entries.Where(e => e.Era is null), e => Assert.Null(e.EraSource));

        Assert.Equal(("classic", ZoneEraSource.Id), (table.EraFor("gfaydark"), table.EntryFor("gfaydark")!.EraSource));
        Assert.Equal(("classic", ZoneEraSource.Curated), (table.EraFor("soltemple"), table.EntryFor("soltemple")!.EraSource));
        Assert.Equal("classic", table.EraFor("oceanoftears"));
        Assert.Equal("pop", table.EraFor("poknowledge"));
        Assert.Equal("cotf", table.EraFor("neriakd"));
        Assert.Null(table.EraFor("newsebexp"));
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
