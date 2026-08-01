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
    string? DefenderOwner = null) : GameEvent;

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
