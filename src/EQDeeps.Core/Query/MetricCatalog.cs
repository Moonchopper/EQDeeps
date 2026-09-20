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

    public static readonly IReadOnlyList<string> LootDefaults =
        ["loots", "platinum", "platPerHour"];

    public static readonly IReadOnlyList<string> ConsiderDefaults = ["considers", "conLevel"];

    /// <summary>
    /// The stance-aware metrics. They only mean anything on a query that groups
    /// by stance (or asks for them explicitly) — everywhere else the stance
    /// clock is never wound and they read zero. Kept out of every source's
    /// defaults for that reason: a stance column belongs on a stance question.
    /// </summary>
    public static readonly IReadOnlyList<string> StanceMetrics =
        ["stanceSeconds", "stanceDps", "stanceUptime"];

    /// <summary>
    /// The one metric that needs the experience/death pairing wound at all
    /// (ADR-022 Decision 3). Kept out of <see cref="DeathDefaults"/> for the
    /// same reason <see cref="StanceMetrics"/> are kept out of every source's
    /// defaults: a deaths query that never asks about credit should never pay
    /// for building it.
    /// </summary>
    public static readonly IReadOnlyList<string> CreditMetrics = ["credited"];

    public static IReadOnlyList<string> DefaultsFor(QuerySource source) => source switch
    {
        QuerySource.Healing => HealingDefaults,
        QuerySource.Tanking => TankingDefaults,
        QuerySource.Casts => CastDefaults,
        QuerySource.Deaths => DeathDefaults,
        QuerySource.Experience => ExperienceDefaults,
        QuerySource.Faction => FactionDefaults,
        QuerySource.Loot => LootDefaults,
        QuerySource.Considers => ConsiderDefaults,
        _ => DamageDefaults,
    };

    /// <summary>
    /// The scope-level context a row's metrics are read against: how long the
    /// whole selection ran, what everyone in it did, and — for stance queries —
    /// how much of that time the owner's stance was known at all.
    /// </summary>
    public readonly record struct MetricScope(
        double RaidSeconds, long GrandTotal, double StanceSeconds = 0);

    /// <summary>Computes one metric from a row's counters and its scope context.</summary>
    public static double Compute(string metric, CounterBag bag, MetricScope scope)
    {
        var raidSeconds = scope.RaidSeconds;
        var grandTotal = scope.GrandTotal;
        var active = bag.ActiveTime.TotalSeconds;
        var held = bag.StanceTime.TotalSeconds;
        return metric switch
        {
            // Stance time is the fair denominator for comparing stances: it
            // counts every second the stance was held inside the scope, so a
            // stance that swings slower cannot look better by being idle.
            "stanceSeconds" => held,
            "stanceDps" => Ratio(bag.Total, held),
            // Against the time the owner's stance was tracked, not raid time,
            // so the column sums to 100% across the stances instead of drifting
            // by however far the two clocks disagree at the scope's edges.
            "stanceUptime" => Percent(held, scope.StanceSeconds),
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
            "credited" => bag.Credited,
            // The row's first and last record, to the log's own one-second
            // resolution — free of any extra bookkeeping, because
            // ActiveTime's merged segments already begin and end exactly
            // there for every source (SealActiveTime runs unconditionally).
            // Encoded as seconds since the Unix epoch with the log's WALL
            // CLOCK read as if it were UTC (ADR-022 Decision 5, ruled by the
            // architect 2026-09-20) — not a real UTC instant; see
            // metrics-and-aggregation.md §5 for why an offset would be a
            // claim the log never made, and why DateTime subtraction being
            // Kind-blind is exactly what makes this pure, exact tick
            // arithmetic. An empty bag (no records in scope) reads 0, which
            // the UI must render as "no data", never as an epoch date.
            "firstAt" => bag.ActiveTime.Segments.Count > 0 ? ToEpochSeconds(bag.ActiveTime.Segments[0].Begin) : 0,
            "lastAt" => bag.ActiveTime.Segments.Count > 0 ? ToEpochSeconds(bag.ActiveTime.Segments[^1].End) : 0,
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
            "loots" => bag.Loots,
            "platinum" => bag.CoinCopper / 1000.0,
            "platPerHour" => Ratio(bag.CoinCopper / 1000.0 * 3600, raidSeconds),
            "considers" => bag.Considers,
            "conLevel" => bag.ConLevelMax,
            _ => 0,
        };
    }

    private static double Ratio(double numerator, double denominator) =>
        denominator > 0 ? numerator / denominator : 0;

    private static double Percent(double numerator, double denominator) =>
        denominator > 0 ? numerator / denominator * 100 : 0;

    /// <summary>
    /// Seconds since the Unix epoch, treating <paramref name="timestamp"/>'s
    /// wall-clock digits as if they were UTC. <see cref="DateTime"/>
    /// subtraction ignores <see cref="DateTime.Kind"/> entirely, so this is
    /// pure tick arithmetic — exact, and reversible to the second by
    /// <c>DateTime.UnixEpoch.AddSeconds(value)</c> in C# or
    /// <c>new Date(value * 1000)</c> in TypeScript.
    /// </summary>
    private static double ToEpochSeconds(DateTime timestamp) => (timestamp - DateTime.UnixEpoch).TotalSeconds;
}
