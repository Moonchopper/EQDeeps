namespace EQDeeps.Core.Gear;

/// <summary>How one slot differs between two snapshots.</summary>
public enum GearChangeKind
{
    /// <summary>A slot that was bare is now filled.</summary>
    Equipped,

    /// <summary>A slot that was filled is now bare.</summary>
    Removed,

    /// <summary>Same item, higher or lower upgrade level ("+2" → "+5").</summary>
    Upgraded,

    /// <summary>A different item in the slot.</summary>
    Replaced,

    /// <summary>Same item, different augments.</summary>
    Reaugmented,
}

/// <summary>One slot's difference, named well enough to render without re-deriving it.</summary>
public sealed record GearSlotChange(
    string SlotKey,
    string Location,
    GearChangeKind Kind,
    GearItem? Before,
    GearItem? After);

/// <summary>
/// A gear change: the instant it was first proven, and what moved. The instant
/// is the <em>later</em> snapshot's capture time — the honest one. The change
/// happened at some unknown point since the previous snapshot; what is known is
/// that by this moment it had.
/// </summary>
public sealed record GearChange(
    DateTime At,
    DateTime PreviousAt,
    IReadOnlyList<GearSlotChange> Slots,
    int UpgradeScoreDelta);

/// <summary>
/// Answers "what was worn then" over an ordered snapshot list, and names the
/// differences between consecutive snapshots.
///
/// <para><b>Forward-only.</b> A snapshot proves the gear at the moment it was
/// captured and nothing before it, so it applies from its own timestamp until
/// the next one, and time before the first snapshot has no answer. The
/// alternative — assuming a snapshot also describes the session that preceded
/// it — labels fights with gear the player may not have been wearing, which is
/// worse than admitting the gap.</para>
/// </summary>
public static class GearHistory
{
    /// <summary>
    /// The snapshot in force at <paramref name="when"/>, or null if that
    /// instant predates every snapshot. A snapshot takes effect at its own
    /// capture instant inclusively.
    /// </summary>
    public static GearSnapshot? EffectiveAt(IReadOnlyList<GearSnapshot> ordered, DateTime when)
    {
        GearSnapshot? effective = null;
        foreach (var snapshot in ordered)
        {
            if (snapshot.CapturedAt > when)
            {
                break;
            }

            effective = snapshot;
        }

        return effective;
    }

    /// <summary>Changes between each consecutive pair. Empty for a single snapshot — nothing to compare.</summary>
    public static List<GearChange> Changes(IReadOnlyList<GearSnapshot> ordered)
    {
        var changes = new List<GearChange>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var slots = Diff(ordered[i - 1], ordered[i]);
            if (slots.Count > 0)
            {
                changes.Add(new GearChange(
                    ordered[i].CapturedAt,
                    ordered[i - 1].CapturedAt,
                    slots,
                    ordered[i].UpgradeScore - ordered[i - 1].UpgradeScore));
            }
        }

        return changes;
    }

    /// <summary>Slot-by-slot difference, keyed by slot so a swap reads as one change, not two.</summary>
    public static List<GearSlotChange> Diff(GearSnapshot before, GearSnapshot after)
    {
        var previous = before.Equipped.ToDictionary(i => i.SlotKey, StringComparer.OrdinalIgnoreCase);
        var current = after.Equipped.ToDictionary(i => i.SlotKey, StringComparer.OrdinalIgnoreCase);

        var changes = new List<GearSlotChange>();

        foreach (var item in after.Equipped)
        {
            if (!previous.TryGetValue(item.SlotKey, out var was))
            {
                changes.Add(new GearSlotChange(
                    item.SlotKey, item.Location, GearChangeKind.Equipped, null, item));
                continue;
            }

            var kind = Classify(was, item);
            if (kind is not null)
            {
                changes.Add(new GearSlotChange(
                    item.SlotKey, item.Location, kind.Value, was, item));
            }
        }

        foreach (var item in before.Equipped.Where(i => !current.ContainsKey(i.SlotKey)))
        {
            changes.Add(new GearSlotChange(
                item.SlotKey, item.Location, GearChangeKind.Removed, item, null));
        }

        return changes;
    }

    private static GearChangeKind? Classify(GearItem before, GearItem after)
    {
        if (before.ItemId != after.ItemId ||
            !before.BaseName.Equals(after.BaseName, StringComparison.OrdinalIgnoreCase))
        {
            return GearChangeKind.Replaced;
        }

        if (before.Plus != after.Plus)
        {
            return GearChangeKind.Upgraded;
        }

        return SameAugments(before.Augments, after.Augments)
            ? null
            : GearChangeKind.Reaugmented;
    }

    /// <summary>
    /// Augment sets compare unordered: the dump lists them by socket, and
    /// moving an augment between two sockets of the same item is not a change
    /// the player made to their gear.
    /// </summary>
    private static bool SameAugments(IReadOnlyList<GearItem> before, IReadOnlyList<GearItem> after)
    {
        if (before.Count != after.Count)
        {
            return false;
        }

        static IEnumerable<string> Keys(IReadOnlyList<GearItem> items) =>
            items.Select(a => $"{a.ItemId}:{a.Name}").OrderBy(k => k, StringComparer.Ordinal);

        return Keys(before).SequenceEqual(Keys(after), StringComparer.Ordinal);
    }
}
