namespace EQDeeps.Core.Gear;

/// <summary>
/// One item, as the inventory dump describes it.
///
/// <para><see cref="Location"/> is the raw slot label ("Head", "Primary",
/// "Any Slot"). It is <em>not</em> unique: paired slots repeat, and EQ Legends
/// has three generic "Any Slot" entries. <see cref="Occurrence"/> disambiguates
/// them by file order, which is the only stable identity the dump offers — so
/// "the second Ear" is comparable across snapshots even though neither the game
/// nor the file names it.</para>
///
/// <para><see cref="Name"/> is verbatim. <see cref="BaseName"/> and
/// <see cref="Plus"/> split the EQ Legends upgrade level out of it
/// ("Short Sword of the Ykesha +5" → "Short Sword of the Ykesha", 5) because a
/// +2 and a +5 of the same sword are the same item to the player and a
/// different item to their parse. That split is what makes a gear diff read
/// "+2 → +5" instead of "removed one thing, added another".</para>
/// </summary>
public sealed record GearItem(
    string Location,
    int Occurrence,
    string Name,
    string BaseName,
    int Plus,
    int ItemId,
    IReadOnlyList<GearItem> Augments)
{
    /// <summary>Slot identity across snapshots: "Ear#2", "Head#1".</summary>
    public string SlotKey => $"{Location}#{Occurrence}";
}

/// <summary>An owned item from the dump's second section — the pool loadouts draw from.</summary>
public sealed record KeyRingEntry(string Category, string Name, int ItemId);

/// <summary>
/// What the character had equipped at one moment, as proven by one
/// <c>/outputfile inventory</c> run.
///
/// <para><see cref="CapturedAt"/> comes from the file's last-write time — the
/// dump carries no timestamp of its own — and is the instant this snapshot
/// starts applying. It never applies backwards: see
/// <see cref="GearHistory.EffectiveAt"/>.</para>
///
/// <para><see cref="Hash"/> covers the equipped set including augments, so
/// re-running the command with nothing changed is recognised as the same
/// snapshot rather than recorded as a second one.</para>
/// </summary>
public sealed record GearSnapshot(
    string Character,
    string Server,
    DateTime CapturedAt,
    IReadOnlyList<GearItem> Equipped,
    IReadOnlyList<KeyRingEntry> KeyRing,
    string Hash)
{
    /// <summary>
    /// Sum of upgrade levels across equipped items and their augments. A crude
    /// number deliberately: it is a progression marker to sort snapshots by,
    /// not a power rating, and nothing in the app should treat it as one.
    /// </summary>
    public int UpgradeScore =>
        Equipped.Sum(i => i.Plus + i.Augments.Sum(a => a.Plus));
}
