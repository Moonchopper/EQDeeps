using EQDeeps.Core.Events;

namespace EQDeeps.Core.Sessions;

/// <summary>
/// One canonical instance per distinct string, per session. The parser is a
/// pure function of one line and cannot know that "Fippy Darkpaw" was already
/// the attacker on the previous four million lines, so it allocates the name
/// afresh every time — and the record store keeps every one of those copies
/// for the life of the session. Measured on a 512 MB synthetic raid log: 16.4
/// million string references over 148 distinct strings, which is more than
/// half of the 1.3 GB the session held. Pooling folds them into one instance
/// each; the parsed copy becomes gen-0 garbage the collector reclaims for
/// nearly nothing.
///
/// <para>Not <see cref="string.Intern"/>: that table is process-global and
/// never shrinks, so every name from every log ever opened would stay resident
/// until exit. This pool belongs to one session and dies with it. Not
/// thread-safe — it is touched only from the session's processing task, which
/// is also the only thing that appends records.</para>
/// </summary>
public sealed class StringPool
{
    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);

    public int Count => _strings.Count;

    /// <summary>The pooled instance equal to <paramref name="value"/>, adding it if new.</summary>
    public string Intern(string value)
    {
        if (_strings.TryGetValue(value, out var pooled))
        {
            return pooled;
        }

        _strings.Add(value, value);
        return value;
    }

    /// <summary>Null-tolerant <see cref="Intern(string)"/> for the optional fields.</summary>
    public string? InternOrNull(string? value) => value is null ? null : Intern(value);

    /// <summary>
    /// The same event with every repeating string replaced by its pooled
    /// instance — the same instance when nothing needed replacing, so a record
    /// built from pooled strings (one restored from the log cache, say) costs
    /// no copy. Chat text is left alone on purpose: it is the one field that
    /// is different on nearly every line, and pooling it would just move the
    /// copies into the dictionary.
    /// </summary>
    public GameEvent Canonicalize(GameEvent evt)
    {
        switch (evt)
        {
            case DamageEvent d:
            {
                var attacker = InternOrNull(d.Attacker);
                var defender = Intern(d.Defender);
                var subType = InternOrNull(d.SubType);
                var attackerOwner = InternOrNull(d.AttackerOwner);
                var defenderOwner = InternOrNull(d.DefenderOwner);
                var school = InternOrNull(d.School);
                return ReferenceEquals(attacker, d.Attacker) && ReferenceEquals(defender, d.Defender)
                    && ReferenceEquals(subType, d.SubType) && ReferenceEquals(attackerOwner, d.AttackerOwner)
                    && ReferenceEquals(defenderOwner, d.DefenderOwner) && ReferenceEquals(school, d.School)
                    ? d
                    : d with
                    {
                        Attacker = attacker,
                        Defender = defender,
                        SubType = subType,
                        AttackerOwner = attackerOwner,
                        DefenderOwner = defenderOwner,
                        School = school,
                    };
            }

            case HealEvent h:
            {
                var healer = InternOrNull(h.Healer);
                var target = Intern(h.Target);
                var spell = InternOrNull(h.Spell);
                var healerOwner = InternOrNull(h.HealerOwner);
                return ReferenceEquals(healer, h.Healer) && ReferenceEquals(target, h.Target)
                    && ReferenceEquals(spell, h.Spell) && ReferenceEquals(healerOwner, h.HealerOwner)
                    ? h
                    : h with { Healer = healer, Target = target, Spell = spell, HealerOwner = healerOwner };
            }

            case DeathEvent d:
            {
                var victim = Intern(d.Victim);
                var killer = InternOrNull(d.Killer);
                return ReferenceEquals(victim, d.Victim) && ReferenceEquals(killer, d.Killer)
                    ? d
                    : d with { Victim = victim, Killer = killer };
            }

            case CastEvent c:
            {
                var caster = Intern(c.Caster);
                var spell = InternOrNull(c.Spell);
                return ReferenceEquals(caster, c.Caster) && ReferenceEquals(spell, c.Spell)
                    ? c
                    : c with { Caster = caster, Spell = spell };
            }

            case WearOffEvent w:
            {
                var spell = Intern(w.Spell);
                var target = Intern(w.Target);
                return ReferenceEquals(spell, w.Spell) && ReferenceEquals(target, w.Target)
                    ? w
                    : w with { Spell = spell, Target = target };
            }

            case AbilityEvent a:
            {
                var user = Intern(a.User);
                var ability = Intern(a.Ability);
                return ReferenceEquals(user, a.User) && ReferenceEquals(ability, a.Ability)
                    ? a
                    : a with { User = user, Ability = ability };
            }

            case StanceEvent s:
            {
                var player = Intern(s.Player);
                var stance = Intern(s.Stance);
                return ReferenceEquals(player, s.Player) && ReferenceEquals(stance, s.Stance)
                    ? s
                    : s with { Player = player, Stance = stance };
            }

            case TauntEvent t:
            {
                var taunter = Intern(t.Taunter);
                var target = Intern(t.Target);
                return ReferenceEquals(taunter, t.Taunter) && ReferenceEquals(target, t.Target)
                    ? t
                    : t with { Taunter = taunter, Target = target };
            }

            case ChatEvent c:
            {
                var sender = Intern(c.Sender);
                var receiver = InternOrNull(c.Receiver);
                var channel = InternOrNull(c.CustomChannel);
                return ReferenceEquals(sender, c.Sender) && ReferenceEquals(receiver, c.Receiver)
                    && ReferenceEquals(channel, c.CustomChannel)
                    ? c
                    : c with { Sender = sender, Receiver = receiver, CustomChannel = channel };
            }

            case ZoneEvent z:
            {
                var zone = InternOrNull(z.ZoneName);
                return ReferenceEquals(zone, z.ZoneName) ? z : z with { ZoneName = zone };
            }

            case MembershipEvent m:
            {
                var player = Intern(m.Player);
                return ReferenceEquals(player, m.Player) ? m : m with { Player = player };
            }

            case WhoEvent w:
            {
                var player = Intern(w.Player);
                var classText = InternOrNull(w.ClassText);
                return ReferenceEquals(player, w.Player) && ReferenceEquals(classText, w.ClassText)
                    ? w
                    : w with { Player = player, ClassText = classText };
            }

            case ResistEvent r:
            {
                var caster = Intern(r.Caster);
                var resister = InternOrNull(r.Resister);
                var spell = Intern(r.Spell);
                return ReferenceEquals(caster, r.Caster) && ReferenceEquals(resister, r.Resister)
                    && ReferenceEquals(spell, r.Spell)
                    ? r
                    : r with { Caster = caster, Resister = resister, Spell = spell };
            }

            case FactionEvent f:
            {
                var faction = Intern(f.Faction);
                return ReferenceEquals(faction, f.Faction) ? f : f with { Faction = faction };
            }

            case LootEvent l:
            {
                var looter = Intern(l.Looter);
                var item = InternOrNull(l.Item);
                var source = InternOrNull(l.Source);
                return ReferenceEquals(looter, l.Looter) && ReferenceEquals(item, l.Item)
                    && ReferenceEquals(source, l.Source)
                    ? l
                    : l with { Looter = looter, Item = item, Source = source };
            }

            case ConsiderEvent c:
            {
                var target = Intern(c.Target);
                var attitude = Intern(c.Attitude);
                return ReferenceEquals(target, c.Target) && ReferenceEquals(attitude, c.Attitude)
                    ? c
                    : c with { Target = target, Attitude = attitude };
            }

            default:
                // ExperienceEvent and LevelEvent carry no strings; anything new
                // passes through unpooled rather than failing, which costs
                // memory, not correctness.
                return evt;
        }
    }
}
