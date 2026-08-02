namespace EQDeeps.Core.Events;

/// <summary>
/// Base type for every typed record produced from one log-line message.
/// Timestamps are attached by the ingestion layer, not here: parsing is a pure
/// function of the message text so it can be tested and parallelized freely.
/// </summary>
public abstract record GameEvent;

/// <summary>
/// Classification of a damage-stream record. Avoidance outcomes (miss/dodge/...)
/// are zero-amount records so defensive counters share the same stream.
/// </summary>
public enum DamageKind
{
    Melee,
    DirectDamage,
    DamageOverTime,
    DamageShield,
    /// <summary>Environmental / spell-as-attacker damage that fits no other bucket.</summary>
    Other,
    Miss,
    Dodge,
    Parry,
    Block,
    Invulnerable,
    Absorb,
}

[Flags]
public enum HitModifiers
{
    None = 0,
    Critical = 1 << 0,
    Lucky = 1 << 1,
    Twincast = 1 << 2,
    Flurry = 1 << 3,
    Riposte = 1 << 4,
    Strikethrough = 1 << 5,
    Rampage = 1 << 6,
    WildRampage = 1 << 7,
    Assassinate = 1 << 8,
    Headshot = 1 << 9,
    SlayUndead = 1 << 10,
    FinishingBlow = 1 << 11,
    DoubleBowShot = 1 << 12,
    Locked = 1 << 13,
}

/// <summary>
/// A damage or damage-attempt record. <see cref="Attacker"/> is null when unknown
/// (environmental damage, ownerless damage shields). <see cref="SubType"/> is the
/// melee skill in display form ("Crushes") or the spell name; null means the line
/// carried no more specific subtype than its <see cref="Kind"/>.
/// <see cref="School"/> is the spell-damage school word (fire/cold/magic/...)
/// when the line carried one; null for melee and unschooled lines.
/// </summary>
public sealed record DamageEvent(
    string? Attacker,
    string Defender,
    uint Amount,
    DamageKind Kind,
    string? SubType,
    HitModifiers Modifiers = HitModifiers.None,
    bool AttackerIsSpell = false,
    string? AttackerOwner = null,
    string? DefenderOwner = null,
    string? School = null) : GameEvent;

/// <summary>
/// A heal record. <see cref="Potential"/> equals <see cref="Landed"/> when the line
/// carried no overheal notation; overheal = Potential - Landed.
/// </summary>
public sealed record HealEvent(
    string? Healer,
    string Target,
    uint Landed,
    uint Potential,
    bool OverTime,
    string? Spell,
    HitModifiers Modifiers = HitModifiers.None) : GameEvent;

public sealed record DeathEvent(string Victim, string? Killer) : GameEvent;

public enum CastKind
{
    Begin,
    Interrupted,
    Fizzle,
}

public sealed record CastEvent(string Caster, string? Spell, CastKind Kind, bool Song = false) : GameEvent;

/// <summary>
/// A buff/debuff fade reported to the log owner by spell name: "Your X spell
/// has worn off." (<see cref="Target"/> = the owner) or "Your X spell has worn
/// off of Soandso." (the owner's buff on someone else). Fades of *received*
/// buffs use per-spell emote text and need the spell database, so they are not
/// events yet.
/// </summary>
public sealed record WearOffEvent(string Spell, string Target) : GameEvent;

/// <summary>An activated discipline/combat ability: "Soandso activates Rest." / "You activate Rest."</summary>
public sealed record AbilityEvent(string User, string Ability) : GameEvent;

public sealed record TauntEvent(string Taunter, string Target, bool Success, bool Improved = false) : GameEvent;

public enum ChatChannel
{
    Say,
    Ooc,
    Auction,
    Shout,
    Group,
    Guild,
    Raid,
    Fellowship,
    Tell,
    /// <summary>A named user channel; see <see cref="ChatEvent.CustomChannel"/>.</summary>
    Custom,
}

/// <summary>
/// A chat message. Senders/receivers are resolved: "You" becomes the log owner's
/// name and cross-server qualifiers (Server.Name) are reduced to the character name.
/// </summary>
public sealed record ChatEvent(
    ChatChannel Channel,
    string Sender,
    string Text,
    string? Receiver = null,
    string? CustomChannel = null) : GameEvent;

/// <summary>Zone entry; a null <see cref="ZoneName"/> marks a transition line (LOADING / Welcome).</summary>
public sealed record ZoneEvent(string? ZoneName) : GameEvent;

/// <summary>
/// Raid/group membership signal ("X has joined the raid.") — a definitive
/// player-verification source for the identity registry.
/// </summary>
public sealed record MembershipEvent(string Player, bool Raid, bool Joined) : GameEvent;

/// <summary>
/// One /who output line: "[60 High Priest] Soandso (High Elf) &lt;Guild&gt;" or
/// "[ANONYMOUS] Soandso". Level/class are null for anonymous players;
/// <see cref="ClassText"/> is the raw bracket text after the level (titles
/// included — resolution against the class list comes with the spell DB).
/// </summary>
public sealed record WhoEvent(string Player, int? Level, string? ClassText) : GameEvent;

/// <summary>A spell resist. Caster perspective is always the log owner in current grammars.</summary>
public sealed record ResistEvent(string Caster, string? Resister, string Spell) : GameEvent;

/// <summary>
/// Experience gained by the log owner. Modern servers log the level-progress
/// delta ("You gain party experience! (1.812%)"); classic servers only
/// announce the event, so <see cref="Percent"/> is null there. AA points are
/// their own line and carry a running total when the server prints one.
/// </summary>
public sealed record ExperienceEvent(
    double? Percent, bool Party, bool AaPoint = false, int? AaTotal = null) : GameEvent;

/// <summary>
/// Faction standing change for the log owner. Modern servers log the numeric
/// adjustment ("has been adjusted by -4."); classic servers only say got
/// better/worse, so <see cref="Delta"/> is null there and <see cref="Better"/>
/// carries the direction. <see cref="Capped"/> marks the "could not possibly
/// get any better/worse" lines, where standing did not actually move.
/// </summary>
public sealed record FactionEvent(
    string Faction, int? Delta, bool Better, bool Capped = false) : GameEvent;

/// <summary>
/// An item loot or coin pickup. <see cref="Item"/> is null for pure coin
/// events; <see cref="Copper"/> (total value in copper, 1 plat = 1000) is null
/// for item-only loots and set for coin pickups and auto-sold loots.
/// <see cref="Source"/> is the corpse/looter-facing origin ("a froglok ton
/// knight", "corpse", "split").
/// </summary>
public sealed record LootEvent(
    string Looter, string? Item, string? Source, long? Copper = null, int Quantity = 1) : GameEvent;

/// <summary>
/// A /consider result: the target's attitude bucket and — on modern servers —
/// its level from the "(Lvl: N)" suffix. Considers are the accessible source
/// of NPC levels (mob-stats groundwork); the threat clause is dropped.
/// </summary>
public sealed record ConsiderEvent(string Target, string Attitude, int? Level) : GameEvent;
