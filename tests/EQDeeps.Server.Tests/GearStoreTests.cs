using EQDeeps.Core.Gear;
using EQDeeps.Server;
using Xunit;

namespace EQDeeps.Server.Tests;

public sealed class GearStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));

    private static readonly DateTime T0 = new(2026, 8, 3, 20, 0, 0);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static GearSnapshot Snapshot(DateTime at, string hash, string item = "Sword") =>
        new("Kizant", "xegony", at,
            [new GearItem("Primary", 1, item, item, 0, 1, [])], [], hash);

    [Fact]
    public void RecordsDistinctSnapshotsAndIgnoresRepeats()
    {
        var store = new GearStore(_dir);

        Assert.True(store.Record(Snapshot(T0, "aaa")));
        // Re-running the dump with nothing changed must not add a second entry,
        // however much later it happens.
        Assert.False(store.Record(Snapshot(T0.AddHours(2), "aaa")));
        Assert.True(store.Record(Snapshot(T0.AddHours(3), "bbb")));

        var list = store.List("Kizant", "xegony");
        Assert.Equal(["aaa", "bbb"], list.Select(s => s.Hash));
    }

    [Fact]
    public void KeepsSnapshotsOrderedOldestFirst()
    {
        var store = new GearStore(_dir);
        store.Record(Snapshot(T0.AddHours(2), "later"));
        store.Record(Snapshot(T0, "earlier"));

        Assert.Equal(["earlier", "later"], store.List("Kizant", "xegony").Select(s => s.Hash));
    }

    [Fact]
    public void RoundTripsThroughDiskWithItemsIntact()
    {
        var augment = new GearItem("Face", 0, "Ruby (Exaltation)", "Ruby (Exaltation)", 0, 77, []);
        var item = new GearItem("Face", 1, "Mask +3", "Mask", 3, 42, [augment]);
        new GearStore(_dir).Record(new GearSnapshot(
            "Kizant", "xegony", T0, [item], [new KeyRingEntry("Equipment", "Spare Cloak", 9)], "h1"));

        var reloaded = Assert.Single(new GearStore(_dir).List("Kizant", "xegony"));

        var restored = Assert.Single(reloaded.Equipped);
        Assert.Equal("Face#1", restored.SlotKey);
        Assert.Equal("Mask", restored.BaseName);
        Assert.Equal(3, restored.Plus);
        Assert.Equal(3, reloaded.UpgradeScore);
        Assert.Equal("Ruby (Exaltation)", Assert.Single(restored.Augments).Name);
        Assert.Equal("Spare Cloak", Assert.Single(reloaded.KeyRing).Name);
    }

    [Fact]
    public void CharactersAreKeptApart()
    {
        var store = new GearStore(_dir);
        store.Record(Snapshot(T0, "kizant-gear"));
        store.Record(new GearSnapshot("Other", "xegony", T0,
            [new GearItem("Primary", 1, "Axe", "Axe", 0, 2, [])], [], "other-gear"));

        Assert.Equal("kizant-gear", Assert.Single(store.List("Kizant", "xegony")).Hash);
        Assert.Equal("other-gear", Assert.Single(store.List("Other", "xegony")).Hash);
        Assert.Empty(store.List("Nobody", "xegony"));
    }

    [Fact]
    public void RepairsSnapshotsAnEarlierBuildFilledWithContainerContents()
    {
        // An early parser counted a personal depot's contents as worn gear.
        // The dumps behind those snapshots are long overwritten, so reading
        // has to repair them rather than recompute them.
        var worn = new GearItem("Primary", 1, "Sword +2", "Sword", 2, 1, []);
        var carried = new GearItem("Personal-Depot1", 1, "Imp Blood", "Imp Blood", 0, 2, []);
        Directory.CreateDirectory(Path.Combine(_dir, "gear"));
        File.WriteAllText(
            Path.Combine(_dir, "gear", "Kizant_xegony.json"),
            System.Text.Json.JsonSerializer.Serialize(new List<GearSnapshot>
            {
                new("Kizant", "xegony", T0, [worn, carried], [], "stale-hash"),
            }));

        var store = new GearStore(_dir);
        var repaired = Assert.Single(store.List("Kizant", "xegony"));

        Assert.Equal("Primary#1", Assert.Single(repaired.Equipped).SlotKey);
        Assert.Equal(2, repaired.UpgradeScore);
        // Re-keyed, or the next identical dump would look like a change.
        Assert.NotEqual("stale-hash", repaired.Hash);
        Assert.Equal(InventoryFileParser.HashOf(repaired.Equipped), repaired.Hash);

        // And written back, so the repair happens once rather than every read.
        Assert.DoesNotContain(
            "Personal-Depot1",
            File.ReadAllText(Path.Combine(_dir, "gear", "Kizant_xegony.json")));
    }

    [Fact]
    public void CorruptHistoryStartsFreshRatherThanFailingForever()
    {
        var store = new GearStore(_dir);
        store.Record(Snapshot(T0, "aaa"));

        File.WriteAllText(Path.Combine(_dir, "gear", "Kizant_xegony.json"), "{not json");

        Assert.Empty(store.List("Kizant", "xegony"));
        Assert.True(store.Record(Snapshot(T0, "aaa")));   // and is usable again
    }

    [Fact]
    public void HistoryIsCappedOldestFirst()
    {
        var store = new GearStore(_dir);
        for (var i = 0; i < 205; i++)
        {
            store.Record(Snapshot(T0.AddMinutes(i), $"hash{i:000}"));
        }

        var list = store.List("Kizant", "xegony");
        Assert.Equal(200, list.Count);
        Assert.Equal("hash005", list[0].Hash);
        Assert.Equal("hash204", list[^1].Hash);
    }
}
