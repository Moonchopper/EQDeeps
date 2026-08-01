using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQDeeps.Core.Query;

public enum QuerySource
{
    Damage,
    Healing,
    Tanking,
    Casts,
    Deaths,
}

public enum Dimension
{
    /// <summary>The row actor: attacker (damage), defender (tanking), healer (healing), caster, victim.</summary>
    Player,

    /// <summary>The opposite party: defender/NPC (damage), attacker (tanking), heal target, killer.</summary>
    Target,

    /// <summary>Spell name or melee skill.</summary>
    Spell,

    /// <summary>Spell school (fire/cold/…) or the damage kind for unschooled records.</summary>
    DamageType,

    /// <summary>The monitored character whose log produced the record.</summary>
    Character,
}

/// <summary>Damage-validity categories users toggle in and out of parses (metrics doc §7).</summary>
public enum ValidityFlag
{
    DamageShield,

    /// <summary>Requires the spell DB to classify; matches nothing until that lands.</summary>
    Bane,
    Headshot,
    Assassinate,
    FinishingBlow,
    SlayUndead,
}

public readonly record struct TimeRange(DateTime Begin, DateTime End)
{
    /// <summary>Log resolution is one second and ranges are inclusive.</summary>
    public double TotalSeconds => (End - Begin).TotalSeconds + 1;
}

/// <summary>
/// What slice of the data a query runs over: selected fights (damage/tanking are
/// keyed to those fights' NPCs; other sources use their merged time ranges), or
/// explicit time ranges. A trim narrows the selection's virtual timeline —
/// "skip the first N seconds" / "only the first M seconds".
/// </summary>
public sealed record QueryScope
{
    /// <summary>Fight ids; null selects all fights.</summary>
    public IReadOnlyList<int>? FightIds { get; init; }

    /// <summary>Explicit ranges instead of fights (healing between pulls, chat archives…).</summary>
    public IReadOnlyList<TimeRange>? TimeRanges { get; init; }

    /// <summary>
    /// The trailing N seconds of the record stream, regardless of fights — the
    /// "what's my DPS right now" scope. Anchored to the newest record's
    /// timestamp (not wall clock), so replays behave identically. Takes
    /// precedence over <see cref="FightIds"/>/<see cref="TimeRanges"/>.
    /// </summary>
    public int? LastSeconds { get; init; }

    public int SkipFirstSeconds { get; init; }

    public int? MaxSeconds { get; init; }
}

public sealed record QueryFilter
{
    /// <summary>Dimension filter: keep rows whose value is in <see cref="Values"/> (or drop, when <see cref="Exclude"/>).</summary>
    public Dimension? Dim { get; init; }

    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>Validity-flag filter on damage records.</summary>
    public ValidityFlag? Flag { get; init; }

    public bool Exclude { get; init; }
}

/// <summary>
/// A serializable description of an aggregation — the heart of the product.
/// Every table, chart, and live meter is a view over one of these.
/// </summary>
public sealed record QuerySpec
{
    public QuerySource Source { get; init; } = QuerySource.Damage;

    public QueryScope Scope { get; init; } = new();

    public IReadOnlyList<Dimension> GroupBy { get; init; } = [Dimension.Player];

    /// <summary>Metric names from the catalog; empty selects the source's default set.</summary>
    public IReadOnlyList<string> Metrics { get; init; } = [];

    public IReadOnlyList<QueryFilter> Filters { get; init; } = [];

    /// <summary>Per-bucket series width; null = whole-scope aggregate only.</summary>
    public int? BucketSeconds { get; init; }

    public bool PetRollup { get; init; } = true;
}

/// <summary>Canonical JSON shape for saved queries, dashboards, and the API.</summary>
public static class QuerySpecJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(QuerySpec spec) => JsonSerializer.Serialize(spec, Options);

    public static QuerySpec? Deserialize(string json) => JsonSerializer.Deserialize<QuerySpec>(json, Options);
}
