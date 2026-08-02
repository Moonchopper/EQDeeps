using EQDeeps.Core.Events;
using EQDeeps.Core.Sessions;

namespace EQDeeps.Core.Query;

public sealed record QueryResult(
    IReadOnlyList<QueryRow> Rows,
    IReadOnlyDictionary<string, double> Totals,
    double RaidSeconds,
    int DataVersion);

public sealed record QueryRow(
    string Key,
    string Label,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyList<QueryRow>? Children,
    IReadOnlyList<SeriesPoint>? Series);

public readonly record struct SeriesPoint(DateTime BucketStart, double Value);

/// <summary>
/// Executes QuerySpecs against one session's state: pure aggregation over
/// (records ∩ time-ranges ∩ filters) into counter bags, then the metric
/// catalog. Damage/tanking are keyed to the selected fights' NPCs; healing,
/// casts, and deaths slice by the selection's merged time ranges (healers heal
/// between pulls). Results are cached by (spec, data version) — flipping a
/// validity toggle or regrouping never reparses anything.
/// </summary>
public sealed class QueryEngine
{
    private readonly RecordStore _records;
    private readonly FightTracker _fights;
    private readonly IdentityRegistry _identity;
    private readonly string _character;
    private readonly Dictionary<string, QueryResult> _cache = [];

    public QueryEngine(RecordStore records, FightTracker fights, IdentityRegistry identity, string character)
    {
        _records = records;
        _fights = fights;
        _identity = identity;
        _character = character;
    }

    public QueryEngine(Session session)
        : this(session.Records, session.Fights, session.Identity, session.Character)
    {
    }

    public QueryResult Execute(QuerySpec spec)
    {
        var version = _records.Version + _fights.Version;
        var cacheKey = QuerySpecJson.Serialize(spec);
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.DataVersion == version)
        {
            return cached;
        }

        var result = ExecuteCore(spec, version);
        if (_cache.Count > 128)
        {
            _cache.Clear();
        }

        _cache[cacheKey] = result;
        return result;
    }

    // ---- scope -------------------------------------------------------------

    /// <summary>One aggregation unit: a fight (damage/tanking) or a plain range.</summary>
    private readonly record struct ScopeUnit(TimeRange Range, string? FightName);

    private List<ScopeUnit> ResolveScope(QueryScope scope, QuerySource source)
    {
        var units = new List<ScopeUnit>();
        if (scope.LastSeconds is > 0 and var lastSeconds)
        {
            if (_records.Count == 0)
            {
                return units;
            }

            var latest = _records[_records.Count - 1].Timestamp;
            units.Add(new ScopeUnit(new TimeRange(latest.AddSeconds(-(lastSeconds - 1)), latest), null));
            return units;
        }

        if (scope.TimeRanges is { Count: > 0 } explicitRanges)
        {
            foreach (var range in explicitRanges)
            {
                units.Add(new ScopeUnit(range, null));
            }
        }
        else if (source is QuerySource.Experience or QuerySource.Faction && scope.FightIds is null)
        {
            // XP and faction largely arrive outside fights (quests, turn-ins)
            // and rate metrics need the real timeline, so an unrestricted scope
            // means the whole record stream — not the union of fight spans.
            if (_records.Count > 0)
            {
                units.Add(new ScopeUnit(
                    new TimeRange(_records[0].Timestamp, _records[_records.Count - 1].Timestamp), null));
            }
        }
        else
        {
            var wanted = scope.FightIds is { Count: > 0 } ids ? new HashSet<int>(ids) : null;
            foreach (var fight in _fights.Fights)
            {
                if (wanted is null || wanted.Contains(fight.Id))
                {
                    units.Add(new ScopeUnit(new TimeRange(fight.BeginTime, fight.LastDamageTime), fight.Name));
                }
            }
        }

        // Selection trim over the merged virtual timeline, then re-intersect
        // each unit so fight keys survive.
        if (scope.SkipFirstSeconds > 0 || scope.MaxSeconds is not null)
        {
            var union = new TimeSegments();
            foreach (var unit in units)
            {
                union.Add(unit.Range.Begin, unit.Range.End);
            }

            var trimmed = union.Trim(scope.SkipFirstSeconds, scope.MaxSeconds);
            var result = new List<ScopeUnit>();
            foreach (var unit in units)
            {
                foreach (var piece in trimmed.Intersect(unit.Range))
                {
                    result.Add(new ScopeUnit(piece, unit.FightName));
                }
            }

            units = result;
        }

        // Healing/casts/deaths/experience/faction aggregate over the merged time
        // ranges, not per fight — collapse to the union so overlaps don't
        // double-count.
        if (source is QuerySource.Healing or QuerySource.Casts or QuerySource.Deaths
            or QuerySource.Experience or QuerySource.Faction)
        {
            var union = new TimeSegments();
            foreach (var unit in units)
            {
                union.Add(unit.Range.Begin, unit.Range.End);
            }

            units = union.Segments.Select(r => new ScopeUnit(r, null)).ToList();
        }

        return units;
    }

    // ---- aggregation -------------------------------------------------------

    private sealed class Node
    {
        public readonly CounterBag Bag = new();
        public readonly Dictionary<int, TimeRange> UnitSpans = [];
        public Dictionary<string, Node>? Children;
        public Dictionary<string, Node>? Actors; // pet-rollup drill-down
        public SortedDictionary<DateTime, double>? Buckets;
    }

    private QueryResult ExecuteCore(QuerySpec spec, int version)
    {
        var units = ResolveScope(spec.Scope, spec.Source);
        var root = new Node();

        for (var unitIndex = 0; unitIndex < units.Count; unitIndex++)
        {
            var unit = units[unitIndex];
            for (var i = _records.LowerBound(unit.Range.Begin); i < _records.Count; i++)
            {
                var record = _records[i];
                if (record.Timestamp > unit.Range.End)
                {
                    break;
                }

                Accumulate(spec, root, unit, unitIndex, record);
            }
        }

        // Convert per-unit spans into merged active-time segments, bottom-up.
        SealActiveTime(root);

        var raidSeconds = root.Bag.ActiveTime.TotalSeconds;
        var metricNames = spec.Metrics.Count > 0 ? spec.Metrics : MetricCatalog.DefaultsFor(spec.Source);
        var grandTotal = root.Bag.Total;

        var rows = EmitRows(root, metricNames, raidSeconds, grandTotal, spec);
        var totals = ComputeMetrics(root.Bag, metricNames, raidSeconds, grandTotal);
        return new QueryResult(rows, totals, raidSeconds, version);
    }

    private void Accumulate(QuerySpec spec, Node root, ScopeUnit unit, int unitIndex, TimedRecord record)
    {
        // Route the record: does it belong to this source, and who is the row actor?
        string? actor;
        DamageEvent? damage = null;
        HealEvent? heal = null;

        switch (spec.Source)
        {
            case QuerySource.Damage:
                if (record.Event is not DamageEvent d1 || d1.Attacker is null)
                {
                    return;
                }

                if (!DefenderMatchesScope(d1, unit))
                {
                    return;
                }

                damage = d1;
                actor = d1.Attacker;
                break;

            case QuerySource.Tanking:
                if (record.Event is not DamageEvent d2)
                {
                    return;
                }

                if (unit.FightName is null ? !IsNpcSide(d2.Attacker) : d2.Attacker != unit.FightName)
                {
                    return;
                }

                damage = d2;
                actor = d2.Defender;
                break;

            case QuerySource.Healing:
                if (record.Event is not HealEvent h)
                {
                    return;
                }

                heal = h;
                actor = h.Healer ?? h.Spell ?? "Unknown";
                break;

            case QuerySource.Casts:
                if (record.Event is not CastEvent)
                {
                    return;
                }

                actor = ((CastEvent)record.Event).Caster;
                break;

            case QuerySource.Deaths:
                if (record.Event is not DeathEvent)
                {
                    return;
                }

                actor = ((DeathEvent)record.Event).Victim;
                break;

            case QuerySource.Experience:
                if (record.Event is not ExperienceEvent)
                {
                    return;
                }

                actor = _character; // XP always belongs to the log owner
                break;

            case QuerySource.Faction:
                if (record.Event is not FactionEvent factionEvent)
                {
                    return;
                }

                actor = factionEvent.Faction; // rows rank the factions themselves
                break;

            default:
                return;
        }

        foreach (var filter in spec.Filters)
        {
            if (!PassesFilter(filter, record.Event, actor, spec))
            {
                return;
            }
        }

        // Walk the grouping levels, accumulating at every node (root = totals).
        // Pet rollup inserts an implicit actor level under merged player rows:
        // merged node carries the combined totals, actor nodes carry the split,
        // and deeper dimensions nest under the actors.
        AddToNode(root, record, damage, heal, unitIndex, spec.BucketSeconds);

        var node = root;
        for (var level = 0; level < spec.GroupBy.Count; level++)
        {
            var dimension = spec.GroupBy[level];
            var key = DimensionKey(dimension, record.Event, actor);
            var rollup = dimension == Dimension.Player && spec.PetRollup;
            var actorName = key;
            if (rollup && _identity.OwnerOf(key) is { } owner)
            {
                key = owner;
            }

            node.Children ??= new Dictionary<string, Node>(StringComparer.Ordinal);
            if (!node.Children.TryGetValue(key, out var child))
            {
                node.Children[key] = child = new Node();
            }

            AddToNode(child, record, damage, heal, unitIndex, spec.BucketSeconds);
            node = child;

            if (rollup)
            {
                child.Actors ??= new Dictionary<string, Node>(StringComparer.Ordinal);
                if (!child.Actors.TryGetValue(actorName, out var actorNode))
                {
                    child.Actors[actorName] = actorNode = new Node();
                }

                AddToNode(actorNode, record, damage, heal, unitIndex, spec.BucketSeconds);
                node = actorNode;
            }
        }
    }

    private static void AddToNode(
        Node node, TimedRecord record, DamageEvent? damage, HealEvent? heal, int unitIndex, int? bucketSeconds)
    {
        if (damage is not null)
        {
            node.Bag.Add(damage);
        }
        else if (heal is not null)
        {
            node.Bag.Add(heal);
        }
        else if (record.Event is CastEvent cast)
        {
            switch (cast.Kind)
            {
                case CastKind.Begin:
                    node.Bag.CastBegins++;
                    break;
                case CastKind.Interrupted:
                    node.Bag.CastInterrupts++;
                    break;
                default:
                    node.Bag.CastFizzles++;
                    break;
            }
        }
        else if (record.Event is DeathEvent)
        {
            node.Bag.Deaths++;
        }
        else if (record.Event is ExperienceEvent xp)
        {
            node.Bag.Add(xp);
        }
        else if (record.Event is FactionEvent faction)
        {
            node.Bag.Add(faction);
        }

        if (node.UnitSpans.TryGetValue(unitIndex, out var span))
        {
            node.UnitSpans[unitIndex] = new TimeRange(span.Begin, record.Timestamp);
        }
        else
        {
            node.UnitSpans[unitIndex] = new TimeRange(record.Timestamp, record.Timestamp);
        }

        if (bucketSeconds is { } width)
        {
            double? amount = damage is not null ? damage.Amount
                : heal is not null ? heal.Landed
                : record.Event is ExperienceEvent bucketXp ? bucketXp.Percent ?? 0
                : record.Event is FactionEvent bucketFaction
                    ? bucketFaction.Capped ? 0 : bucketFaction.Delta ?? (bucketFaction.Better ? 1 : -1)
                : null;
            if (amount is { } value)
            {
                node.Buckets ??= [];
                var offset = record.Timestamp.Ticks / TimeSpan.TicksPerSecond;
                var bucketStart = new DateTime((offset - offset % width) * TimeSpan.TicksPerSecond);
                node.Buckets[bucketStart] = node.Buckets.GetValueOrDefault(bucketStart) + value;
            }
        }
    }

    private static void SealActiveTime(Node node)
    {
        foreach (var span in node.UnitSpans.Values)
        {
            node.Bag.ActiveTime.Add(span.Begin, span.End);
        }

        if (node.Children is not null)
        {
            foreach (var child in node.Children.Values)
            {
                SealActiveTime(child);
            }
        }

        if (node.Actors is not null)
        {
            foreach (var actor in node.Actors.Values)
            {
                SealActiveTime(actor);
            }
        }
    }

    // ---- routing helpers ---------------------------------------------------

    private bool DefenderMatchesScope(DamageEvent damage, ScopeUnit unit)
    {
        if (unit.FightName is not null)
        {
            return damage.Defender == unit.FightName;
        }

        // Raw time-range scope has no fight key to classify sides, so mirror the
        // fight tracker's assumption rules: the defender must not be on the
        // players' side, and the attacker must not be a known NPC — unknown
        // defenders (swarm adds, un-killed nameds) still count.
        return !_identity.IsPlayerSide(damage.Defender) &&
               !IsNpcSide(damage.Attacker) &&
               !damage.AttackerIsSpell;
    }

    private bool IsNpcSide(string? name) =>
        name is not null && !_identity.IsPlayerSide(name) && _identity.IsDefinitelyNpc(name);

    private string DimensionKey(Dimension dimension, GameEvent evt, string actor)
    {
        return dimension switch
        {
            Dimension.Player => actor,
            Dimension.Character => _character,
            Dimension.Target => evt switch
            {
                DamageEvent d => d.Defender == actor ? d.Attacker ?? "Unknown" : d.Defender,
                HealEvent h => h.Target,
                DeathEvent de => de.Killer ?? "Unknown",
                _ => "Unknown",
            },
            Dimension.Spell => evt switch
            {
                DamageEvent d => d.SubType ?? DamageKindLabel(d.Kind),
                HealEvent h => h.Spell ?? "Unknown",
                CastEvent c => c.Spell ?? "Unknown",
                ExperienceEvent x => x.AaPoint ? "AA point" : x.Party ? "party" : "solo",
                FactionEvent f => f.Capped ? "capped" : f.Better ? "up" : "down",
                _ => "Unknown",
            },
            Dimension.DamageType => evt is DamageEvent dt
                ? dt.School ?? DamageKindLabel(dt.Kind)
                : "Unknown",
            _ => "Unknown",
        };
    }

    private static string DamageKindLabel(DamageKind kind) => kind switch
    {
        DamageKind.Melee => "melee",
        DamageKind.DirectDamage => "directDamage",
        DamageKind.DamageOverTime => "damageOverTime",
        DamageKind.DamageShield => "damageShield",
        DamageKind.Other => "other",
        _ => kind.ToString(),
    };

    private bool PassesFilter(QueryFilter filter, GameEvent evt, string actor, QuerySpec spec)
    {
        bool matches;
        if (filter.Flag is { } flag)
        {
            matches = evt is DamageEvent damage && MatchesFlag(flag, damage);
        }
        else if (filter.Dim is { } dim && filter.Values is { Count: > 0 })
        {
            var key = DimensionKey(dim, evt, actor);

            // With pet rollup on, a player filter means the owner AND their
            // pets — matching raw actor names would silently drop pet damage.
            if (dim == Dimension.Player && spec.PetRollup && _identity.OwnerOf(key) is { } owner)
            {
                key = owner;
            }

            matches = filter.Values.Contains(key, StringComparer.Ordinal);
        }
        else
        {
            return true; // empty filter: no-op
        }

        return filter.Exclude ? !matches : matches;
    }

    private static bool MatchesFlag(ValidityFlag flag, DamageEvent damage) => flag switch
    {
        ValidityFlag.DamageShield => damage.Kind == DamageKind.DamageShield,
        ValidityFlag.Bane => damage.School == "bane",
        ValidityFlag.Headshot => (damage.Modifiers & HitModifiers.Headshot) != 0,
        ValidityFlag.Assassinate => (damage.Modifiers & HitModifiers.Assassinate) != 0,
        ValidityFlag.FinishingBlow => (damage.Modifiers & HitModifiers.FinishingBlow) != 0,
        ValidityFlag.SlayUndead => (damage.Modifiers & HitModifiers.SlayUndead) != 0,
        _ => false,
    };

    // ---- output ------------------------------------------------------------

    private static List<QueryRow> EmitRows(
        Node parent, IReadOnlyList<string> metricNames, double raidSeconds, long grandTotal, QuerySpec spec)
    {
        if (parent.Children is null)
        {
            return [];
        }

        var rows = new List<QueryRow>(parent.Children.Count);
        foreach (var (key, node) in parent.Children)
        {
            var label = key;
            List<QueryRow>? children;

            if (node.Actors is { Count: > 0 } actors)
            {
                var hasPets = actors.Count > 1 || !actors.ContainsKey(key);
                if (hasPets)
                {
                    // "Owner +Pets": drill to the actor breakdown; deeper
                    // dimensions continue under each actor.
                    label = key + " +Pets";
                    children = actors
                        .Select(a => new QueryRow(
                            a.Key, a.Key,
                            ComputeMetrics(a.Value.Bag, metricNames, raidSeconds, grandTotal),
                            Nullable(EmitRows(a.Value, metricNames, raidSeconds, grandTotal, spec)),
                            EmitSeries(a.Value)))
                        .OrderByDescending(r => r.Metrics.GetValueOrDefault("total"))
                        .ToList();
                }
                else
                {
                    // Single actor identical to the row: flatten the actor level.
                    children = Nullable(EmitRows(actors[key], metricNames, raidSeconds, grandTotal, spec));
                }
            }
            else
            {
                children = Nullable(EmitRows(node, metricNames, raidSeconds, grandTotal, spec));
            }

            rows.Add(new QueryRow(
                key, label,
                ComputeMetrics(node.Bag, metricNames, raidSeconds, grandTotal),
                children,
                EmitSeries(node)));
        }

        return rows.OrderByDescending(r => r.Metrics.GetValueOrDefault("total")).ToList();
    }

    private static List<QueryRow>? Nullable(List<QueryRow> rows) => rows.Count > 0 ? rows : null;

    private static IReadOnlyList<SeriesPoint>? EmitSeries(Node node) =>
        node.Buckets?.Select(b => new SeriesPoint(b.Key, b.Value)).ToList();

    private static Dictionary<string, double> ComputeMetrics(
        CounterBag bag, IReadOnlyList<string> metricNames, double raidSeconds, long grandTotal)
    {
        var metrics = new Dictionary<string, double>(metricNames.Count, StringComparer.Ordinal);
        foreach (var name in metricNames)
        {
            metrics[name] = MetricCatalog.Compute(name, bag, raidSeconds, grandTotal);
        }

        return metrics;
    }
}
