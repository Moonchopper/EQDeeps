using EQDeeps.Core.Events;

namespace EQDeeps.Core.Sessions;

/// <summary>
/// Segments the record stream into fights (metrics doc §1). Fights are keyed by
/// NPC name and created lazily on the first valid combat record where exactly
/// one side is an NPC; a name the identity registry can't classify is assumed
/// NPC when the other side is player-side (and the phantom fight is deleted if
/// the name is later verified as a player). Time comes exclusively from record
/// timestamps — replay and live behave identically.
/// </summary>
public sealed class FightTracker
{
    /// <summary>Combat-inactivity timeout for fights that have damage.</summary>
    public static readonly TimeSpan FightTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Hard inactivity cap for fights kept alive by non-damage activity.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Fight-list grouping gap ("Break Time" threshold).</summary>
    public static readonly TimeSpan GroupTimeout = TimeSpan.FromSeconds(120);

    /// <summary>How long a player-cast spell can attribute spell-as-attacker damage.</summary>
    public static readonly TimeSpan RecentSpellWindow = TimeSpan.FromSeconds(300);

    private readonly IdentityRegistry _identity;
    private readonly List<Fight> _fights = [];
    private readonly Dictionary<string, Fight> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _recentPlayerSpells = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingCorrections = new();
    private int _nextId = 1;

    public FightTracker(IdentityRegistry identity)
    {
        _identity = identity;

        // The registry is shared across sessions, so these can fire from another
        // session's thread; corrections queue up and apply on our processing task.
        _identity.PlayerVerified += name => _pendingCorrections.Enqueue(name);
        _identity.PetMapped += (pet, _) => _pendingCorrections.Enqueue(pet);
    }

    /// <summary>All fights, open and closed, in begin order.</summary>
    public IReadOnlyList<Fight> Fights => _fights;

    public IReadOnlyCollection<Fight> ActiveFights => _active.Values;

    /// <summary>
    /// Version stamp bumped whenever fight state changes (creation, update,
    /// close, deletion) — cache-invalidation hook for the query engine.
    /// </summary>
    public int Version { get; private set; }

    public void Process(DateTime timestamp, GameEvent evt)
    {
        ApplyPendingCorrections();
        ExpireFights(timestamp);
        switch (evt)
        {
            case DamageEvent damage:
                HandleDamage(timestamp, damage);
                break;
            case DeathEvent death:
                HandleDeath(timestamp, death);
                break;
            case TauntEvent taunt:
                HandleTaunt(timestamp, taunt);
                break;
            case CastEvent { Kind: CastKind.Begin, Spell: not null } cast
                when !_identity.IsDefinitelyNpc(cast.Caster):
                _recentPlayerSpells[cast.Spell] = timestamp;
                break;
            case ZoneEvent:
                CloseAll();
                break;
        }
    }

    /// <summary>
    /// Deletes fights whose key turned out to be a verified player or a mapped
    /// pet — they were misclassifications, not real fights.
    /// </summary>
    public void ApplyPendingCorrections()
    {
        while (_pendingCorrections.TryDequeue(out var name))
        {
            RemoveFightsNamed(name);
        }
    }

    /// <summary>Applies the inactivity timeouts as of <paramref name="now"/> (also the live-tick hook).</summary>
    public void ExpireFights(DateTime now)
    {
        List<string>? expired = null;
        foreach (var (name, fight) in _active)
        {
            var idle = now - fight.LastActivityTime;
            if (idle > MaxTimeout || (idle > FightTimeout && fight.HasDamage))
            {
                (expired ??= []).Add(name);
            }
        }

        if (expired is not null)
        {
            foreach (var name in expired)
            {
                Close(_active[name]);
            }
        }
    }

    /// <summary>
    /// Groups fights into pull chains: a new group starts when the gap from the
    /// previous fight's last damage reaches <see cref="GroupTimeout"/>.
    /// </summary>
    public static List<List<Fight>> Group(IReadOnlyList<Fight> fights, TimeSpan? gapThreshold = null)
    {
        var gap = gapThreshold ?? GroupTimeout;
        var groups = new List<List<Fight>>();
        List<Fight>? current = null;
        DateTime lastEnd = default;
        foreach (var fight in fights)
        {
            if (current is null || fight.BeginTime - lastEnd >= gap)
            {
                current = [];
                groups.Add(current);
            }

            current.Add(fight);
            if (fight.LastDamageTime > lastEnd)
            {
                lastEnd = fight.LastDamageTime;
            }
        }

        return groups;
    }

    private void HandleDamage(DateTime timestamp, DamageEvent damage)
    {
        var attacker = damage.Attacker;
        if (attacker is null)
        {
            return; // unknown source (environment, ownerless DS) — no fight key
        }

        if (damage.AttackerOwner is not null)
        {
            _identity.MapPetToOwner(attacker, damage.AttackerOwner);
        }

        if (damage.DefenderOwner is not null)
        {
            _identity.MapPetToOwner(damage.Defender, damage.DefenderOwner);
        }

        var defender = damage.Defender;
        var attackerSide = SideOf(attacker, damage.AttackerIsSpell, timestamp);
        var defenderSide = SideOf(defender, isSpell: false, timestamp);

        if (damage.AttackerIsSpell && attackerSide != Side.Player)
        {
            return; // spell with no recent player cast: environmental, no fight
        }

        // Exactly one side must be an NPC; an unknown facing a player-side entity
        // is assumed NPC (corrected later if it verifies as a player).
        bool playersAttack;
        if (defenderSide == Side.Npc && attackerSide != Side.Npc)
        {
            playersAttack = true;
        }
        else if (attackerSide == Side.Npc && defenderSide != Side.Npc)
        {
            playersAttack = false;
        }
        else if (attackerSide == Side.Player && defenderSide == Side.Unknown)
        {
            playersAttack = true;
        }
        else if (defenderSide == Side.Player && attackerSide == Side.Unknown)
        {
            playersAttack = false;
        }
        else
        {
            return; // player↔player, NPC↔NPC, or two unknowns — no fight
        }

        var npcName = playersAttack ? defender : attacker;
        var fight = GetOrCreate(npcName, timestamp);
        fight.LastActivityTime = timestamp;
        fight.LastDamageTime = timestamp;
        fight.HasDamage = true;

        var second = fight.Seconds.TryGetValue(timestamp, out var bucket) ? bucket : default;
        if (playersAttack)
        {
            fight.DamageTotal += damage.Amount;
            second.Damage += damage.Amount;
            Accumulate(fight.DamageByActor, attacker, damage.Amount);
        }
        else
        {
            fight.TankingTotal += damage.Amount;
            second.Tanking += damage.Amount;
            Accumulate(fight.TankingByDefender, defender, damage.Amount);
        }

        fight.Seconds[timestamp] = second;
        Version++;
    }

    private void HandleDeath(DateTime timestamp, DeathEvent death)
    {
        if (_active.TryGetValue(death.Victim, out var fight))
        {
            fight.Dead = true;
            fight.LastActivityTime = timestamp;
            fight.LastDamageTime = timestamp;
            Close(fight);
        }

        // Death grammars are strong identity evidence: "died." is NPC-only, and a
        // player-side kill marks the victim as NPC. Pets die as "X`s pet" and are
        // excluded via the possessive check inside the registry.
        if (death.Victim.EndsWith(" pet", StringComparison.Ordinal))
        {
            return;
        }

        if (death.Killer is null || _identity.IsPlayerSide(death.Killer))
        {
            _identity.AddKnownNpc(death.Victim);
        }
    }

    private void HandleTaunt(DateTime timestamp, TauntEvent taunt)
    {
        if (_active.TryGetValue(taunt.Target, out var fight))
        {
            fight.LastActivityTime = timestamp;
            fight.TauntCount++;
            Version++;
            return;
        }

        // A taunt can open a fight (tanks pull with taunt before any damage);
        // it survives up to MaxTimeout without damage.
        if (!_identity.IsDefinitelyNpc(taunt.Target) && !_identity.IsPlayerSide(taunt.Taunter))
        {
            return;
        }

        if (_identity.IsPlayerSide(taunt.Target))
        {
            return;
        }

        fight = GetOrCreate(taunt.Target, timestamp);
        fight.LastActivityTime = timestamp;
        fight.TauntCount++;
        Version++;
    }

    private enum Side
    {
        Unknown,
        Player,
        Npc,
    }

    private Side SideOf(string name, bool isSpell, DateTime timestamp)
    {
        if (isSpell)
        {
            // "spell as attacker" lines attribute to the caster's side when a
            // player-side entity cast that spell recently.
            return _recentPlayerSpells.TryGetValue(name, out var castAt) &&
                   timestamp - castAt <= RecentSpellWindow
                ? Side.Player
                : Side.Unknown;
        }

        if (_identity.IsPlayerSide(name))
        {
            return Side.Player;
        }

        return _identity.IsDefinitelyNpc(name) ? Side.Npc : Side.Unknown;
    }

    private Fight GetOrCreate(string npcName, DateTime timestamp)
    {
        if (_active.TryGetValue(npcName, out var fight))
        {
            return fight;
        }

        fight = new Fight(_nextId++, npcName, timestamp);
        _active[npcName] = fight;
        _fights.Add(fight);
        Version++;
        return fight;
    }

    private void Close(Fight fight)
    {
        fight.Closed = true;
        _active.Remove(fight.Name);
        Version++;
    }

    private void CloseAll()
    {
        foreach (var fight in _active.Values.ToList())
        {
            Close(fight);
        }
    }

    private void RemoveFightsNamed(string name)
    {
        _active.Remove(name);
        var removed = _fights.RemoveAll(f => f.Name == name);
        if (removed > 0)
        {
            Version++;
        }
    }

    private static void Accumulate(Dictionary<string, ActorTotals> totals, string actor, uint amount)
    {
        if (!totals.TryGetValue(actor, out var entry))
        {
            totals[actor] = entry = new ActorTotals();
        }

        entry.Total += amount;
        entry.Hits++;
    }
}
