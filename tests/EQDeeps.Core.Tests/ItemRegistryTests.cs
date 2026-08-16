using Xunit;
using EQDeeps.Core.Items;

namespace EQDeeps.Core.Tests;

public class ItemRegistryTests
{
    [Theory]
    [InlineData("Fine Steel Rapier +2", "Fine Steel Rapier")]
    [InlineData("Guise of the Deceiver (Exaltation)", "Guise of the Deceiver")]
    [InlineData("Guise of the Deceiver +3 (Exaltation)", "Guise of the Deceiver")]
    [InlineData("  Rusty Broad Sword +4 ", "Rusty Broad Sword")]
    [InlineData("Wind Rune Kala", "Wind Rune Kala")]
    [InlineData("Wind Rune 7", "Wind Rune 7")]
    [InlineData("Plus +", "Plus +")]
    public void StripRemovesLegendsDecorationsOnly(string name, string expected)
    {
        Assert.Equal(expected, ItemNames.Strip(name));
    }

    [Fact]
    public void KeyFoldsCaseWhitespaceAndDecoration()
    {
        Assert.Equal("raw-hide mask", ItemNames.Key("Raw-Hide Mask +1"));
        Assert.Equal(ItemNames.Key("Raw-hide  Mask"), ItemNames.Key("RAW-HIDE MASK (Exaltation)"));
        Assert.Equal("", ItemNames.Key("   "));
    }

    [Fact]
    public void LearnAndObserveMeetOnOneRow()
    {
        var registry = new ItemRegistry();
        var t = new DateTime(2026, 8, 16, 10, 0, 0);

        Assert.True(registry.Observe("Fine Steel Rapier +2", t, ItemSource.Looted));
        Assert.False(registry.Observe("fine steel rapier", t.AddMinutes(5), ItemSource.Sold, 1));
        Assert.True(registry.Learn("Fine Steel Rapier", 7352, 762, ItemSource.LootFilter));

        var record = registry.Find("Fine Steel Rapier +3");
        Assert.NotNull(record);
        Assert.Equal("Fine Steel Rapier", record!.Name);
        Assert.Equal(7352, record.Id);
        Assert.Equal(762, record.IconId);
        Assert.Equal(t, record.FirstSeen);
        Assert.Equal(t.AddMinutes(5), record.LastSeen);
        Assert.Equal(1, record.Looted);
        Assert.Equal(1, record.Sold);
        Assert.Equal(ItemSource.Looted | ItemSource.Sold | ItemSource.LootFilter, record.Sources);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void FileCasingOutranksLogCasing()
    {
        var registry = new ItemRegistry();
        registry.Observe("RAW-HIDE mask", DateTime.Now, ItemSource.Looted);
        registry.Learn("Raw-Hide Mask", 2138, 700, ItemSource.LootFilter);
        Assert.Equal("Raw-Hide Mask", registry.Find("raw-hide mask")!.Name);

        // A second file does not rename; it may only correct the id.
        Assert.False(registry.Learn("Raw-hide Mask", 2138, null, ItemSource.Inventory) && registry.Find("raw-hide mask")!.Name != "Raw-Hide Mask");
        Assert.Equal("Raw-Hide Mask", registry.Find("raw-hide mask")!.Name);
        Assert.Equal(700, registry.Find("raw-hide mask")!.IconId);
    }

    [Fact]
    public void LearnReportsChangeOnlyWhenSomethingIsNew()
    {
        var registry = new ItemRegistry();
        Assert.True(registry.Learn("Bone Chips", 13005, 800, ItemSource.LootFilter));
        Assert.False(registry.Learn("Bone Chips", 13005, 800, ItemSource.LootFilter));
        Assert.True(registry.Learn("Bone Chips", 13005, 800, ItemSource.Inventory));
        var version = registry.Version;
        registry.Learn("Bone Chips", 13005, 800, ItemSource.Inventory);
        Assert.Equal(version, registry.Version);
    }

    [Fact]
    public void SnapshotRoundTrips()
    {
        var registry = new ItemRegistry();
        registry.Learn("Bone Chips", 13005, 800, ItemSource.LootFilter);
        registry.Observe("Bone Chips", new DateTime(2026, 1, 1), ItemSource.Looted, 4);
        registry.Observe("Spider Silk", new DateTime(2026, 1, 2), ItemSource.Looted, 2);

        var restored = ItemRegistry.FromSnapshot(registry.Snapshot());
        Assert.Equal(2, restored.Count);
        var chips = restored.Find("bone chips")!;
        Assert.Equal(13005, chips.Id);
        Assert.Equal(4, chips.Looted);
        Assert.Equal("Bone Chips", chips.Name);
        // A file-named row keeps that status: a later log sighting does not rename it.
        restored.Observe("BONE CHIPS", new DateTime(2026, 1, 3), ItemSource.Looted);
        Assert.Equal("Bone Chips", restored.Find("bone chips")!.Name);
    }

    [Fact]
    public void LootFilterFileParsesRowsAndSkipsJunk()
    {
        const string text = "#ITEM_ID^FILTER_ID^ICON_ID^ITEM_NAME\r\n" +
                            "13374^4^819^Froglok Poison Gland\r\n" +
                            "5016^4^605^Rusty Broad Sword +4\r\n" +
                            "garbage line\r\n" +
                            "0^4^1^Nothing\r\n" +
                            "177779^2^966^Wind Rune Kala\r\n";
        var rows = LootFilterFile.Parse(text);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new LootFilterRow(13374, 4, 819, "Froglok Poison Gland"), rows[0]);
        Assert.Equal("Rusty Broad Sword +4", rows[1].Name);
        Assert.Equal(966, rows[2].IconId);
        Assert.Equal(Path.Combine(@"C:\EQ", "userdata", "LF_Moonchopper_qeynos.ini"),
            LootFilterFile.PathFor(@"C:\EQ", "Moonchopper", "qeynos"));
    }

    [Fact]
    public void InventoryDumpParsesRowsAndSkipsEmpties()
    {
        const string text = "Location\tName\tID\tCount\tSlots\n" +
                            "Any Slot\tMithril Two-Handed Sword +2\t5401\t1\t10\n" +
                            "Face-Slot8\tGuise of the Deceiver (Exaltation)\t2469\t1\t10\n" +
                            "General1\tEmpty\t0\t0\t0\n" +
                            "Bank2\tBone Chips\t13005\t20\t0\n";
        var rows = InventoryDump.Parse(text);
        Assert.Equal(3, rows.Count);
        Assert.Equal(5401, rows[0].ItemId);
        Assert.Equal("Guise of the Deceiver (Exaltation)", rows[1].Name);
        Assert.Equal(20, rows[2].Count);
        Assert.Equal(Path.Combine(@"C:\EQ", "Moonchopper_qeynos-Inventory.txt"),
            InventoryDump.PathFor(@"C:\EQ", "Moonchopper", "qeynos"));
    }

    [Fact]
    public void ScannerFindsWholeNamesLongestFirst()
    {
        var scanner = new ItemMentionScanner(["Fine Steel", "Fine Steel Rapier", "Journeyman's Boots", "Spell: Holy Armor", "Egg"]);

        Assert.Equal(["Fine Steel Rapier"], scanner.Find("go to befallen, you can also get Fine Steel Rapier +6"));
        Assert.Equal(["Fine Steel"], scanner.Find("selling fine steel cheap"));
        Assert.Equal(["Journeyman's Boots"], scanner.Find("WTB 'Journeyman's Boots' pst!"));
        Assert.Equal(["Spell: Holy Armor"], scanner.Find("anyone have Spell: Holy Armor?"));
        Assert.Equal(["Journeyman's Boots", "Fine Steel Rapier"], scanner.Find("journeyman's boots or a Fine Steel Rapier."));
    }

    [Fact]
    public void ScannerHoldsOneWordNamesToTheirOwnCase()
    {
        var scanner = new ItemMentionScanner(["Egg", "Horn", "Bone Chips"]);
        Assert.Empty(scanner.Find("i had an egg for breakfast"));
        Assert.Equal(["Egg"], scanner.Find("looking for an Egg"));
        // A one-word name inside a capitalised phrase is some other item.
        Assert.Empty(scanner.Find("Where does Efreeti War Horn come from?"));
        Assert.Empty(scanner.Find("Denon's Horn of Disaster"));
        Assert.Equal(["Horn"], scanner.Find("wtb Horn pst"));
        Assert.Equal(["Bone Chips"], scanner.Find("wts bone chips"));
        Assert.Empty(scanner.Find(""));
    }
}
