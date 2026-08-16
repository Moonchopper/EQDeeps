using EQDeeps.Core.Reference;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// The shapes a reference site publishes, and how a name from the log is
/// matched to one of them (F30, ADR-020). Every case here is the real shape,
/// trimmed: the point of a pure parser is that the whole format is under test
/// without anyone's server being involved.
/// </summary>
public class NpcReferenceTests
{
    private const string Index = """
        [["Fippy Darkpaw (5)","n",2119],
         ["a rabid kobold (6)","n",1201],
         ["a rabid kobold (9)","n",1202],
         ["Rusty Dagger","i",5020],
         ["a nameless one","n",1300],
         ["Gate","s",36],
         ["broken row"],
         ["a bad id","n","x"]]
        """;

    private const string Shard = """
        {"2119":{"id":2119,"name":"Fippy Darkpaw","level":5,"maxLevel":5,"hp":75,"ac":19,
          "race":"Gnoll","className":"Warrior","faction":"Sabertooths of Blackburrow","respawn":640,
          "minDmg":1,"maxDmg":14,"specials":["Summon"],
          "loot":[[5020,"Rusty Battle Axe",8.25,569,"6/42"],[13025,"Patch of Gnoll Fur",55,556,""]],
          "zones":[{"zone":"qeynos2","longName":"North Qeynos","spawnPoints":1,"locs":[[481.2,1210.8,3.1]]}]},
         "2120":{"id":2120,"name":"a gnoll pup","hp":12},
         "2121":{"name":"no id at all","hp":5},
         "2122":"not an object"}
        """;

    [Fact]
    public void IndexKeepsNpcsAndSplitsTheirLevels()
    {
        var entries = NpcReferenceFormat.ParseIndex(Index);

        // Items, spells and malformed rows are not NPCs and are not kept.
        Assert.Equal(4, entries.Count);
        Assert.Equal(new NpcIndexEntry("Fippy Darkpaw", 5, 2119), entries[0]);
        Assert.Equal(new NpcIndexEntry("a rabid kobold", 9, 1202), entries[2]);
        // A name with no parenthesised level keeps its name and offers none.
        Assert.Equal(new NpcIndexEntry("a nameless one", null, 1300), entries[3]);
    }

    [Theory]
    [InlineData("Fippy Darkpaw (5)", "Fippy Darkpaw", 5)]
    [InlineData("a rabid kobold (12)", "a rabid kobold", 12)]
    [InlineData("a gnoll (guard)", "a gnoll (guard)", null)]
    [InlineData("Nobody", "Nobody", null)]
    [InlineData("a thing (0)", "a thing (0)", null)]
    public void LevelIsSplitOffOnlyWhenItIsANumber(string listed, string name, int? level)
    {
        var (bare, parsed) = NpcReferenceFormat.SplitLevel(listed);
        Assert.Equal(name, bare);
        Assert.Equal(level, parsed);
    }

    [Fact]
    public void ShardCarriesTheStatBlockAndTheLootTable()
    {
        var byId = NpcReferenceFormat.ParseShard(Shard);

        // The row with no id and the one that is not an object are skipped.
        Assert.Equal(2, byId.Count);
        var fippy = byId[2119];
        Assert.Equal("Fippy Darkpaw", fippy.Name);
        Assert.Equal(5, fippy.Level);
        Assert.Equal(75, fippy.Hp);
        Assert.Equal(19, fippy.Ac);
        Assert.Equal("Gnoll", fippy.Race);
        Assert.Equal("Warrior", fippy.Class);
        Assert.Equal("Sabertooths of Blackburrow", fippy.Faction);
        Assert.Equal(640, fippy.RespawnSeconds);
        Assert.Equal(["Summon"], fippy.Specials);

        Assert.Equal(2, fippy.Loot.Count);
        Assert.Equal(new NpcLootLine(5020, "Rusty Battle Axe", 8.25, 569, "6/42"), fippy.Loot[0]);
        // An empty damage string is "not a weapon", not an empty weapon.
        Assert.Null(fippy.Loot[1].Damage);

        var zone = Assert.Single(fippy.Zones);
        Assert.Equal("qeynos2", zone.ShortName);
        Assert.Equal("North Qeynos", zone.LongName);
        Assert.Equal([481.2, 1210.8, 3.1], zone.Locations[0]);

        // Everything but id and name may be missing.
        Assert.Null(byId[2120].Level);
        Assert.Empty(byId[2120].Loot);
    }

    [Fact]
    public void AShapeItDoesNotKnowIsEmpty_NeverAThrow()
    {
        Assert.Empty(NpcReferenceFormat.ParseIndex("{\"not\":\"an array\"}"));
        Assert.Empty(NpcReferenceFormat.ParseIndex("this is not json at all"));
        Assert.Empty(NpcReferenceFormat.ParseShard("[1,2,3]"));
        Assert.Empty(NpcReferenceFormat.ParseShard(""));
    }

    [Fact]
    public void ShardsAreAThousandIdsWide()
    {
        Assert.Equal(2, NpcReferenceFormat.ShardOf(2119));
        Assert.Equal(0, NpcReferenceFormat.ShardOf(999));
        Assert.Equal("/data/npcs/58.json", NpcReferenceFormat.ShardPath(58007));
    }

    [Fact]
    public void AConsideredLevelPicksBetweenVariantsOfOneName()
    {
        var index = new NpcIndex(NpcReferenceFormat.ParseIndex(Index));
        Assert.Equal(3, index.NameCount);
        Assert.Equal(4, index.EntryCount);

        // No level to go on: the first listing, and honest about it.
        var guessed = index.Resolve("a rabid kobold", [], out var exactWithout);
        Assert.Equal(6, guessed!.Level);
        Assert.False(exactWithout);

        // One listing only, with a /consider that agrees: corroborated, even
        // though there was nothing to choose between.
        Assert.Equal(2119, index.Resolve("Fippy Darkpaw", [5], out var single)!.Id);
        Assert.True(single);
        index.Resolve("Fippy Darkpaw", [40], out var wrongLevel);
        Assert.False(wrongLevel);

        // A /consider settles which one it was.
        var resolved = index.Resolve("a rabid kobold", [9], out var exact);
        Assert.Equal(1202, resolved!.Id);
        Assert.True(exact);

        // Close counts; miles off does not, and says so rather than lying.
        Assert.Equal(1202, index.Resolve("a rabid kobold", [10], out var near)!.Id);
        Assert.True(near);
        index.Resolve("a rabid kobold", [40], out var far);
        Assert.False(far);

        // Case never decides — the loot and death grammars disagree about it.
        Assert.Equal(2119, index.Resolve("fippy darkpaw", [], out _)!.Id);
        Assert.Null(index.Resolve("nobody at all", [], out _));
    }

    [Fact]
    public void ANameIsOneRowHoweverManyPlacesItStandsIn()
    {
        // The real shape of the problem: a site lists the same mob once per
        // zone, so one name carries several listings at the SAME level.
        var index = new NpcIndex(
        [
            new NpcIndexEntry("a ghoul", 13, 20022), // Kithicor Forest
            new NpcIndexEntry("a ghoul", 13, 21014), // West Commonlands
            new NpcIndexEntry("a ghoul", 13, 36038), // Befallen
            new NpcIndexEntry("a ghoul", 14, 22184),
            new NpcIndexEntry("a ghoul", 24, 63011),
        ]);

        var row = Assert.Single(index.Browse("ghoul"));
        Assert.Equal("a ghoul", row.Name);
        Assert.Equal(13, row.MinLevel);
        Assert.Equal(24, row.MaxLevel);
        Assert.Equal(5, row.Listings);
        // Three levels, not five addresses.
        Assert.Equal([13, 14, 24], row.PerLevel.Select(v => v.Level).ToArray());
        Assert.Equal(20022, row.PerLevel[0].Id);
    }

    [Fact]
    public void SearchRanksExactThenPrefixThenContains()
    {
        var index = new NpcIndex(
        [
            new NpcIndexEntry("kobold", 1, 1),
            new NpcIndexEntry("a rabid kobold", 6, 2),
            new NpcIndexEntry("kobold guard", 4, 3),
            new NpcIndexEntry("a gnoll", 2, 4),
        ]);

        var names = index.Search("kobold").Select(m => m.Name).ToArray();
        Assert.Equal(["kobold", "kobold guard", "a rabid kobold"], names);
        Assert.Empty(index.Search(""));
        Assert.Single(index.Search("kobold", limit: 1));

        // One row per name, carrying every variant listed under it.
        var variants = new NpcIndex(NpcReferenceFormat.ParseIndex(Index)).Search("rabid");
        Assert.Equal(2, Assert.Single(variants).Variants.Count);
    }
}
