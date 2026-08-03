using EQDeeps.Core.Gear;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// Parsing the /outputfile inventory dump. The corpus fixture is a real EQ
/// Legends dump, so the structural claims below (augments as -Slot rows,
/// repeated slot labels, "+N" upgrade levels) are asserted against what the
/// game actually writes rather than against a guess at it.
/// </summary>
public class InventoryFileParserTests
{
    private static readonly DateTime CapturedAt = new(2026, 8, 3, 18, 59, 0);

    private static GearSnapshot RealDump()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "inventory-legends.txt");
        var snapshot = InventoryFileParser.Parse(
            File.ReadAllLines(path), "Moonchopper", "qeynos", CapturedAt);
        Assert.NotNull(snapshot);
        return snapshot;
    }

    private static GearItem Slot(GearSnapshot snapshot, string slotKey) =>
        Assert.Single(snapshot.Equipped, i => i.SlotKey == slotKey);

    [Fact]
    public void ReadsEquippedItemsAndSkipsContainers()
    {
        var snapshot = RealDump();

        Assert.Equal("Skull-Shaped Barbute +7", Slot(snapshot, "Head#1").Name);
        Assert.Equal("Shimmering Ruby Stiletto +5", Slot(snapshot, "Primary#1").Name);
        Assert.Equal(5820, Slot(snapshot, "Primary#1").ItemId);

        // Bags, banks and the cursor are carried, not worn.
        Assert.DoesNotContain(snapshot.Equipped, i => i.Location.StartsWith("General"));
        Assert.DoesNotContain(snapshot.Equipped, i => i.Location.StartsWith("Bank"));
        Assert.DoesNotContain(snapshot.Equipped, i => i.Location.StartsWith("SharedBank"));
        Assert.DoesNotContain(snapshot.Equipped, i => i.Location == "Held");

        // Nothing bagged leaks in, however deeply nested.
        Assert.DoesNotContain(snapshot.Equipped, i => i.Name == "Shin Greaves +5");
        Assert.DoesNotContain(
            snapshot.Equipped.SelectMany(i => i.Augments),
            a => a.Name == "Runed Mithril Bracer (Exaltation)");
    }

    [Fact]
    public void NumbersRepeatedSlotsInFileOrder()
    {
        var snapshot = RealDump();

        // Ear, Wrist and Fingers come in pairs, and EQ Legends adds a generic
        // "Any Slot". Position is the only identity the dump offers.
        Assert.Equal("Golden Earring +2", Slot(snapshot, "Ear#1").Name);
        Assert.Equal("Jade Earring +4", Slot(snapshot, "Ear#2").Name);
        Assert.Equal("Pristine Studded Leather Bracer", Slot(snapshot, "Wrist#1").Name);
        Assert.Equal("Silver-Plated Bracer +6", Slot(snapshot, "Wrist#2").Name);
        Assert.Equal("Adamantite Band +2", Slot(snapshot, "Fingers#1").Name);
        Assert.Equal("Djarn's Amethyst Ring +1", Slot(snapshot, "Fingers#2").Name);

        // "Any Slot-Slot2" and friends are that item's augment sockets, not
        // further "Any Slot"s — only the two top-level rows get numbered.
        Assert.Equal("Mithril Two-Handed Sword +2", Slot(snapshot, "Any Slot#1").Name);
        Assert.Equal("Brigandine Tunic +3", Slot(snapshot, "Any Slot#2").Name);
        Assert.Equal(2, snapshot.Equipped.Count(i => i.Location == "Any Slot"));
    }

    [Fact]
    public void AttachesAugmentsToTheSlotAboveThem()
    {
        var snapshot = RealDump();

        var face = Slot(snapshot, "Face#1");
        Assert.Equal(
            ["Carved Ivory Mask (Exaltation)", "Guise of the Deceiver (Exaltation)"],
            face.Augments.Select(a => a.Name));

        // The second Wrist carries the augment; the first has none, and the
        // rows for both are interleaved in the file.
        Assert.Empty(Slot(snapshot, "Wrist#1").Augments);
        Assert.Equal("Serpentine Bracer (Exaltation)",
            Assert.Single(Slot(snapshot, "Wrist#2").Augments).Name);

        Assert.Empty(Slot(snapshot, "Head#1").Augments);
    }

    [Fact]
    public void SplitsTheUpgradeLevelOutOfTheName()
    {
        var snapshot = RealDump();

        var sword = Slot(snapshot, "Secondary#1");
        Assert.Equal("Short Sword of the Ykesha +5", sword.Name);
        Assert.Equal("Short Sword of the Ykesha", sword.BaseName);
        Assert.Equal(5, sword.Plus);

        // An un-upgraded item keeps its name as its base name.
        var bracer = Slot(snapshot, "Wrist#1");
        Assert.Equal("Pristine Studded Leather Bracer", bracer.BaseName);
        Assert.Equal(0, bracer.Plus);

        Assert.True(snapshot.UpgradeScore > 0);
    }

    [Fact]
    public void ReadsTheKeyRingSection()
    {
        var snapshot = RealDump();

        Assert.Contains(snapshot.KeyRing,
            e => e is { Category: "Equipment", Name: "Dark Reaver +4", ItemId: 5404 });
        Assert.Contains(snapshot.KeyRing,
            e => e is { Category: "Augmentation", Name: "Moonstone Ring (Exaltation)" });
        Assert.DoesNotContain(snapshot.KeyRing, e => e.Name == "Empty");
    }

    [Fact]
    public void HashCoversEquipmentAndAugmentsButNotBags()
    {
        var lines = File.ReadAllLines(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "inventory-legends.txt"));
        var baseline = InventoryFileParser.Parse(lines, "A", "b", CapturedAt)!;

        // Re-running the command with nothing changed must look identical, even
        // captured at a different moment — that is what stops the store
        // recording a second snapshot for every dump.
        var again = InventoryFileParser.Parse(lines, "A", "b", CapturedAt.AddHours(3))!;
        Assert.Equal(baseline.Hash, again.Hash);

        // Bank and bag churn is not a gear change.
        var looted = lines
            .Select(l => l.StartsWith("Bank1\t") ? "Bank1\tRusty Dagger\t1234\t1\t10" : l)
            .ToArray();
        Assert.Equal(baseline.Hash, InventoryFileParser.Parse(looted, "A", "b", CapturedAt)!.Hash);

        // Swapping an augment is.
        var reaugmented = lines
            .Select(l => l.StartsWith("Face-Slot8\t")
                ? "Face-Slot8\tSomething Else (Exaltation)\t9999\t1\t10"
                : l)
            .ToArray();
        Assert.NotEqual(baseline.Hash, InventoryFileParser.Parse(reaugmented, "A", "b", CapturedAt)!.Hash);
    }

    [Theory]
    [InlineData("")]                                   // empty file
    [InlineData("Location\tName\tID\tCount\tSlots")]    // header only
    [InlineData("garbage\nmore garbage")]              // not an inventory dump at all
    [InlineData("Location\tName\tID\nHead\tEmpty\t0")]  // nothing equipped
    public void UnusableInputYieldsNoSnapshot(string content)
    {
        Assert.Null(InventoryFileParser.Parse(
            content.Split('\n'), "A", "b", CapturedAt));
    }

    [Fact]
    public void ToleratesRaggedRows()
    {
        string[] lines =
        [
            "Location\tName\tID\tCount\tSlots",
            "Head\tHelm +1\t100\t1\t10",
            "Chest\tTruncated\t101",          // missing trailing columns
            "Legs\tNoId",                     // too few fields entirely
            "Feet\tBoots\tnot-a-number\t1\t10",
        ];

        var snapshot = InventoryFileParser.Parse(lines, "A", "b", CapturedAt)!;

        Assert.Equal(["Head#1", "Chest#1", "Feet#1"], snapshot.Equipped.Select(i => i.SlotKey));
        Assert.Equal(0, Slot(snapshot, "Feet#1").ItemId);   // unparseable id, not a crash
    }

    [Fact]
    public void FileNameMatchesTheGamesConvention() =>
        Assert.Equal("Moonchopper_qeynos-Inventory.txt",
            InventoryFileParser.FileNameFor("Moonchopper", "qeynos"));
}
