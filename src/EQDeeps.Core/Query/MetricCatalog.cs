namespace EQDeeps.Core.Query;

/// <summary>
/// The derived-metric formulas (metrics doc §5), implemented once and shared by
/// tables, charts, and the live meter. Values are unrounded doubles — formatting
/// (K/M/B, one decimal) is presentation. Division by zero yields 0.
/// </summary>
public static class MetricCatalog
{
    public static readonly IReadOnlyList<string> DamageDefaults =
    [
        "total", "dps", "sdps", "percentOfTotal", "hits", "avgHit", "maxHit",
        "critRate", "luckyRate", "twincastRate", "activeSeconds",
    ];

    public static readonly IReadOnlyList<string> HealingDefaults =
    [
        "total", "extra", "potential", "overhealRate", "dps", "percentOfTotal",
        "hits", "avgHit", "maxHit", "critRate", "activeSeconds",
    ];

    public static readonly IReadOnlyList<string> TankingDefaults =
    [
        "total", "dps", "percentOfTotal", "hits", "meleeAttempts", "undefendedRate",
        "avgHit", "maxHit", "activeSeconds",
    ];

    public static readonly IReadOnlyList<string> CastDefaults = ["casts", "interrupts", "fizzles"];

    public static readonly IReadOnlyList<string> DeathDefaults = ["deaths"];

    public static readonly IReadOnlyList<string> ExperienceDefaults =
        ["xpPercent", "xpPerHour", "xpGains", "aaPoints"];

    public static readonly IReadOnlyList<string> FactionDefaults =
        ["factionNet", "factionUps", "factionDowns", "factionCapped"];

    public static IReadOnlyList<string> DefaultsFor(QuerySource source) => source switch
    {
        QuerySource.Healing => HealingDefaults,
        QuerySource.Tanking => TankingDefaults,
        QuerySource.Casts => CastDefaults,
        QuerySource.Deaths => DeathDefaults,
        QuerySource.Experience => ExperienceDefaults,
        QuerySource.Faction => FactionDefaults,
        _ => DamageDefaults,
    };

    /// <summary>
    /// Computes one metric from a row's counters. <paramref name="raidSeconds"/>
    /// and <paramref name="grandTotal"/> are scope-level context.
    /// </summary>
    public static double Compute(string metric, CounterBag bag, double raidSeconds, long grandTotal)
    {
        var active = bag.ActiveTime.TotalSeconds;
        return metric switch
        {
            "total" => bag.Total,
            "extra" => bag.Extra,
            "potential" => bag.Total + bag.Extra,
            "hits" => bag.Hits,
            "critHits" => bag.CritHits,
            "luckyHits" => bag.LuckyHits,
            "twincastHits" => bag.TwincastHits,
            "maxHit" => bag.MaxHit,
            "minHit" => bag.MinHit,
            "maxPotentialHit" => bag.MaxPotentialHit,
            "activeSeconds" => active,
            "raidSeconds" => raidSeconds,
            "dps" => Ratio(bag.Total, active),
            "sdps" => Ratio(bag.Total, raidSeconds),
            "pdps" => Ratio(bag.Total + bag.Extra, active),
            "avgHit" => Ratio(bag.Total, bag.Hits),
            "avgCrit" => Ratio(bag.CritTotal - bag.LuckyTotal, bag.CritHits - bag.LuckyHits),
            "avgLucky" => Ratio(bag.LuckyTotal, bag.LuckyHits),
            "critRate" => Percent(bag.CritHits, bag.Hits),
            "luckyRate" => Percent(bag.LuckyHits, bag.CritHits),
            "twincastRate" => Math.Min(100,
                Percent(bag.TwincastDirectHits * 2 + bag.TwincastDotHits, bag.SpellHits)),
            "flurryRate" => Percent(bag.FlurryHits, bag.RegularMeleeHits),
            "rampageRate" => Percent(bag.RampageHits, bag.MeleeHits),
            "riposteRate" => Percent(bag.RiposteHits, bag.MeleeHits),
            "doubleBowRate" => Percent(bag.DoubleBowHits, bag.BowHits),
            "strikethroughRate" => Percent(bag.StrikethroughHits, bag.MeleeHits),
            "meleeHitRate" => Percent(bag.MeleeHits, bag.MeleeAttempts),
            "meleeAccuracy" => Percent(
                bag.MeleeHits,
                bag.MeleeAttempts - bag.Parries - bag.Dodges - bag.Blocks - bag.Invulnerable - bag.Absorbs),
            "undefendedRate" => Percent(
                bag.MeleeAttempts - bag.Misses - bag.Dodges - bag.Parries - bag.Blocks - bag.Absorbs - bag.Invulnerable,
                bag.MeleeAttempts),
            "overhealRate" => Percent(bag.Extra, bag.Total + bag.Extra),
            "percentOfTotal" => Percent(bag.Total, grandTotal),
            "meleeAttempts" => bag.MeleeAttempts,
            "misses" => bag.Misses,
            "dodges" => bag.Dodges,
            "parries" => bag.Parries,
            "blocks" => bag.Blocks,
            "absorbs" => bag.Absorbs,
            "invulnerable" => bag.Invulnerable,
            "hotHits" => bag.HotHits,
            "deaths" => bag.Deaths,
            "casts" => bag.CastBegins,
            "interrupts" => bag.CastInterrupts,
            "fizzles" => bag.CastFizzles,
            "taunts" => bag.Taunts,
            "xpPercent" => bag.XpPercent,
            "xpPerHour" => Ratio(bag.XpPercent * 3600, raidSeconds),
            "xpGains" => bag.XpGains,
            "aaPoints" => bag.AaPoints,
            "factionNet" => bag.FactionNet,
            "factionUps" => bag.FactionUps,
            "factionDowns" => bag.FactionDowns,
            "factionCapped" => bag.FactionCapped,
            _ => 0,
        };
    }

    private static double Ratio(double numerator, double denominator) =>
        denominator > 0 ? numerator / denominator : 0;

    private static double Percent(double numerator, double denominator) =>
        denominator > 0 ? numerator / denominator * 100 : 0;
}
