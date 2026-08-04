using EQDeeps.Core.Gear;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// Attribution and diffing. The boundary cases matter more than the happy path:
/// a snapshot that applies one second too early labels a fight with gear the
/// player was not wearing, which is the failure this feature exists to avoid.
/// </summary>
public class GearHistoryTests
{
    private static readonly DateTime T0 = new(2026, 8, 3, 20, 0, 0);

    private static GearItem Item(string slot, string baseName, int plus = 0, int id = 1,
        params string[] augments) =>
        new(slot, 1, plus > 0 ? $"{baseName} +{plus}" : baseName, baseName, plus, id,
            augments.Select(a => new GearItem(slot, 0, a, a, 0, id * 100 + a.Length, [])).ToList());

    private static GearSnapshot Snapshot(DateTime at, params GearItem[] equipped) =>
        new("A", "b", at, equipped, [], string.Join(
            "|", equipped.Select(i => $"{i.SlotKey}={i.Name}")));

    // ---- attribution ----------------------------------------------------

    [Fact]
    public void NothingIsKnownBeforeTheFirstSnapshot()
    {
        var history = new[] { Snapshot(T0, Item("Primary", "Sword")) };

        Assert.Null(GearHistory.EffectiveAt(history, T0.AddSeconds(-1)));
        Assert.Null(GearHistory.EffectiveAt([], T0));
    }

    [Fact]
    public void ASnapshotAppliesFromItsOwnInstantOnward()
    {
        var first = Snapshot(T0, Item("Primary", "Sword", 2));
        var second = Snapshot(T0.AddMinutes(30), Item("Primary", "Sword", 5));
        var history = new[] { first, second };

        Assert.Equal(first, GearHistory.EffectiveAt(history, T0));                     // inclusive
        Assert.Equal(first, GearHistory.EffectiveAt(history, T0.AddMinutes(29)));
        Assert.Equal(second, GearHistory.EffectiveAt(history, T0.AddMinutes(30)));     // inclusive
        Assert.Equal(second, GearHistory.EffectiveAt(history, T0.AddYears(1)));        // no expiry
    }

    [Fact]
    public void AdjacentSnapshotsResolveToTheLaterOne()
    {
        var first = Snapshot(T0, Item("Primary", "Sword", 2));
        var second = Snapshot(T0.AddSeconds(1), Item("Primary", "Sword", 3));

        Assert.Equal(first, GearHistory.EffectiveAt([first, second], T0));
        Assert.Equal(second, GearHistory.EffectiveAt([first, second], T0.AddSeconds(1)));
    }

    // ---- diffing --------------------------------------------------------

    [Fact]
    public void AnUpgradeIsOneChange()
    {
        var before = Snapshot(T0, Item("Primary", "Sword", 2));
        var after = Snapshot(T0.AddHours(1), Item("Primary", "Sword", 5));

        var change = Assert.Single(GearHistory.Diff(before, after));

        Assert.Equal(GearChangeKind.Upgraded, change.Kind);
        Assert.Equal("Primary#1", change.SlotKey);
        Assert.Equal(2, change.Before!.Plus);
        Assert.Equal(5, change.After!.Plus);
    }

    [Fact]
    public void SwappingAnItemReadsAsOneReplacementNotTwoChanges()
    {
        var before = Snapshot(T0, Item("Primary", "Sword", id: 1));
        var after = Snapshot(T0.AddHours(1), Item("Primary", "Axe", id: 2));

        var change = Assert.Single(GearHistory.Diff(before, after));

        Assert.Equal(GearChangeKind.Replaced, change.Kind);
        Assert.Equal("Sword", change.Before!.BaseName);
        Assert.Equal("Axe", change.After!.BaseName);
    }

    [Fact]
    public void EmptyingAndFillingSlotsAreNamedSeparately()
    {
        var before = Snapshot(T0, Item("Primary", "Sword"), Item("Head", "Helm"));
        var after = Snapshot(T0.AddHours(1), Item("Primary", "Sword"), Item("Back", "Cloak"));

        var changes = GearHistory.Diff(before, after);

        Assert.Equal(GearChangeKind.Equipped,
            Assert.Single(changes, c => c.SlotKey == "Back#1").Kind);
        Assert.Equal(GearChangeKind.Removed,
            Assert.Single(changes, c => c.SlotKey == "Head#1").Kind);
        Assert.DoesNotContain(changes, c => c.SlotKey == "Primary#1");
    }

    [Fact]
    public void AugmentChangesCountButReorderingThemDoesNot()
    {
        var before = Snapshot(T0, Item("Face", "Mask", augments: ["Ruby", "Opal"]));
        var reordered = Snapshot(T0.AddHours(1), Item("Face", "Mask", augments: ["Opal", "Ruby"]));
        var swapped = Snapshot(T0.AddHours(2), Item("Face", "Mask", augments: ["Ruby", "Jade"]));

        Assert.Empty(GearHistory.Diff(before, reordered));
        Assert.Equal(GearChangeKind.Reaugmented,
            Assert.Single(GearHistory.Diff(before, swapped)).Kind);
    }

    [Fact]
    public void IdenticalSnapshotsProduceNoChange() =>
        Assert.Empty(GearHistory.Diff(
            Snapshot(T0, Item("Primary", "Sword", 3)),
            Snapshot(T0.AddDays(1), Item("Primary", "Sword", 3))));

    // ---- change list ----------------------------------------------------

    [Fact]
    public void ChangesPairConsecutiveSnapshotsAndCarryTheUpgradeDelta()
    {
        var history = new[]
        {
            Snapshot(T0, Item("Primary", "Sword", 2)),
            Snapshot(T0.AddHours(1), Item("Primary", "Sword", 2)),   // no gear change
            Snapshot(T0.AddHours(2), Item("Primary", "Sword", 6)),
        };

        var change = Assert.Single(GearHistory.Changes(history));

        // Dated at the snapshot that proved it, and carrying how far back the
        // uncertainty runs — the change happened somewhere in between.
        Assert.Equal(T0.AddHours(2), change.At);
        Assert.Equal(T0.AddHours(1), change.PreviousAt);
        Assert.Equal(4, change.UpgradeScoreDelta);
    }

    [Fact]
    public void ASingleSnapshotHasNothingToCompare() =>
        Assert.Empty(GearHistory.Changes([Snapshot(T0, Item("Primary", "Sword"))]));
}
