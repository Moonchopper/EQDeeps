using EQDeeps.Core.Events;

namespace EQDeeps.Core.Query;

/// <summary>
/// The monotone counters every derived metric is a pure function of (metrics
/// doc §2). One bag per result row; accumulation is orientation-agnostic — on
/// the damage side the avoidance counters mean "my swings were dodged", on the
/// tanking side "I dodged" — the row key supplies the orientation.
/// </summary>
public sealed class CounterBag
{
    private static readonly HashSet<string> RegularMeleeSubTypes = new(StringComparer.Ordinal)
    {
        // The skills that can flurry — the flurry-rate denominator.
        "Bites", "Claws", "Crushes", "Pierces", "Punches", "Slashes", "Hits",
    };

    public long Total;
    public long Extra;
    public long Hits;
    public uint MaxHit;
    public uint MinHit;
    public uint MaxPotentialHit;

    public long CritHits;
    public long CritTotal;
    public long LuckyHits;
    public long LuckyTotal;
    public long TwincastHits;

    public long MeleeAttempts;
    public long MeleeHits;
    public long RegularMeleeHits;
    public long BowHits;
    public long DoubleBowHits;
    public long FlurryHits;
    public long RampageHits;
    public long RiposteHits;
    public long StrikethroughHits;

    public long AssassinateHits;
    public long HeadshotHits;
    public long FinishingBlowHits;
    public long SlayUndeadHits;

    public long Misses;
    public long Dodges;
    public long Parries;
    public long Blocks;
    public long Absorbs;
    public long Invulnerable;

    public long SpellHits;
    public long DirectHits;
    public long DotHits;
    public long TwincastDirectHits;
    public long TwincastDotHits;

    public long HotHits;
    public long Deaths;
    public long CastBegins;
    public long CastInterrupts;
    public long CastFizzles;
    public long Taunts;

    /// <summary>Level-progress percent summed across gains (0 on classic logs).</summary>
    public double XpPercent;
    public long XpGains;
    public long AaPoints;

    /// <summary>Signed faction sum; classic no-number lines count as ±1.</summary>
    public long FactionNet;
    public long FactionUps;
    public long FactionDowns;
    public long FactionCapped;

    public long Loots;
    public long CoinCopper;

    public readonly TimeSegments ActiveTime = new();

    public void Add(ExperienceEvent xp)
    {
        if (xp.AaPoint)
        {
            AaPoints++;
            return;
        }

        XpGains++;
        XpPercent += xp.Percent ?? 0;
    }

    public void Add(FactionEvent faction)
    {
        if (faction.Capped)
        {
            FactionCapped++; // standing didn't move
            return;
        }

        FactionNet += faction.Delta ?? (faction.Better ? 1 : -1);
        if (faction.Better)
        {
            FactionUps++;
        }
        else
        {
            FactionDowns++;
        }
    }

    public void Add(LootEvent loot)
    {
        if (loot.Item is not null)
        {
            Loots += loot.Quantity;
        }

        CoinCopper += loot.Copper ?? 0;
    }

    public void Add(DamageEvent damage)
    {
        switch (damage.Kind)
        {
            case DamageKind.Melee:
                RecordLanded(damage.Amount);
                MeleeAttempts++;
                MeleeHits++;
                if (damage.SubType is not null && RegularMeleeSubTypes.Contains(damage.SubType))
                {
                    RegularMeleeHits++;
                }
                else if (damage.SubType == "Shoots")
                {
                    BowHits++;
                }

                AddModifierCounters(damage.Modifiers, damage.Amount);
                break;

            case DamageKind.DirectDamage:
                RecordLanded(damage.Amount);
                SpellHits++;
                DirectHits++;
                if ((damage.Modifiers & HitModifiers.Twincast) != 0)
                {
                    TwincastDirectHits++;
                }

                AddModifierCounters(damage.Modifiers, damage.Amount);
                break;

            case DamageKind.DamageOverTime:
                RecordLanded(damage.Amount);
                SpellHits++;
                DotHits++;
                if ((damage.Modifiers & HitModifiers.Twincast) != 0)
                {
                    TwincastDotHits++;
                }

                AddModifierCounters(damage.Modifiers, damage.Amount);
                break;

            case DamageKind.DamageShield:
            case DamageKind.Other:
                RecordLanded(damage.Amount);
                AddModifierCounters(damage.Modifiers, damage.Amount);
                break;

            case DamageKind.Miss:
                CountAttempt(damage);
                Misses++;
                break;
            case DamageKind.Dodge:
                CountAttempt(damage);
                Dodges++;
                break;
            case DamageKind.Parry:
                CountAttempt(damage);
                Parries++;
                break;
            case DamageKind.Block:
                CountAttempt(damage);
                Blocks++;
                break;
            case DamageKind.Invulnerable:
                CountAttempt(damage);
                Invulnerable++;
                break;
            case DamageKind.Absorb:
                CountAttempt(damage);
                Absorbs++;
                break;
        }
    }

    public void Add(HealEvent heal)
    {
        Hits++;
        Total += heal.Landed;
        Extra += heal.Potential - heal.Landed;
        if (heal.OverTime)
        {
            HotHits++;
        }

        if (heal.Landed > MaxHit)
        {
            MaxHit = heal.Landed;
        }

        if (MinHit == 0 || heal.Landed < MinHit)
        {
            MinHit = heal.Landed;
        }

        if (heal.Potential > MaxPotentialHit)
        {
            MaxPotentialHit = heal.Potential;
        }

        AddCritCounters(heal.Modifiers, heal.Landed);
        if ((heal.Modifiers & HitModifiers.Twincast) != 0)
        {
            TwincastHits++;
        }
    }

    private void RecordLanded(uint amount)
    {
        Hits++;
        Total += amount;
        if (amount > MaxHit)
        {
            MaxHit = amount;
        }

        if (MinHit == 0 || (amount > 0 && amount < MinHit))
        {
            MinHit = amount;
        }
    }

    private void CountAttempt(DamageEvent damage)
    {
        // Avoidance records with a melee-verb subtype are swing attempts; skin
        // absorbs and rune lines (null subtype) count only their own counter.
        if (damage.SubType is not null)
        {
            MeleeAttempts++;
        }
    }

    private void AddModifierCounters(HitModifiers modifiers, long amount)
    {
        AddCritCounters(modifiers, amount);
        if ((modifiers & HitModifiers.Twincast) != 0)
        {
            TwincastHits++;
        }

        if ((modifiers & HitModifiers.Flurry) != 0)
        {
            FlurryHits++;
        }

        if ((modifiers & (HitModifiers.Rampage | HitModifiers.WildRampage)) != 0)
        {
            RampageHits++;
        }

        if ((modifiers & HitModifiers.Riposte) != 0)
        {
            RiposteHits++;
        }

        if ((modifiers & HitModifiers.Strikethrough) != 0)
        {
            StrikethroughHits++;
        }

        if ((modifiers & HitModifiers.DoubleBowShot) != 0)
        {
            DoubleBowHits++;
        }

        if ((modifiers & HitModifiers.Assassinate) != 0)
        {
            AssassinateHits++;
        }

        if ((modifiers & HitModifiers.Headshot) != 0)
        {
            HeadshotHits++;
        }

        if ((modifiers & HitModifiers.FinishingBlow) != 0)
        {
            FinishingBlowHits++;
        }

        if ((modifiers & HitModifiers.SlayUndead) != 0)
        {
            SlayUndeadHits++;
        }
    }

    private void AddCritCounters(HitModifiers modifiers, long amountForTotals)
    {
        if ((modifiers & HitModifiers.Critical) != 0)
        {
            CritHits++;
            CritTotal += amountForTotals;
        }

        if ((modifiers & HitModifiers.Lucky) != 0)
        {
            LuckyHits++;
            LuckyTotal += amountForTotals;
        }
    }

}
