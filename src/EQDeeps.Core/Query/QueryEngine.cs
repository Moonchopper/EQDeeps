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
    private StanceTimeline? _stances;
    private int _stancesVersion = -1;
    private PresenceTimeline? _presence;
    private int _presenceVersion = -1;

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
            // A range picked off the fight list is still a range, but combat
            // must keep aggregating per fight inside it: "DPS over these five
            // pulls" has to mean what it meant when five fights were selected
            // directly, not damage averaged across the downtime between them.
            // Progression sources take the range whole - XP, faction and loot
            // land between pulls, which is the entire point of a range.
            var perFight = source is not (QuerySource.Experience or QuerySource.Faction
                or QuerySource.Loot or QuerySource.Considers);
            foreach (var range in explicitRanges)
            {
                var matched = false;
                if (perFight)
                {
                    foreach (var fight in _fights.Fights)
                    {
                        if (fight.BeginTime > range.End || fight.LastDamageTime < range.Begin)
                        {
                            continue;
                        }

                        // Clipped to the range: a fight straddling the edge
                        // contributes only the part the user actually framed.
                        units.Add(new ScopeUnit(
                            new TimeRange(
                                fight.BeginTime > range.Begin ? fight.BeginTime : range.Begin,
                                fight.LastDamageTime < range.End ? fight.LastDamageTime : range.End),
                            fight.Name));
                        matched = true;
                    }
                }

                // Nothing fought in it (pure downtime, or a progression
                // source): the range itself is the unit.
                if (!matched)
                {
                    units.Add(new ScopeUnit(range, null));
                }
            }
        }
        else if (source is QuerySource.Experience or QuerySource.Faction or QuerySource.Loot
                     or QuerySource.Considers &&
                 scope.FightIds is null)
        {
            // XP, faction, loot, and considers largely arrive outside fight
            // spans (quests, turn-ins, looting/conning around the pull) and
            // rate metrics need the real timeline, so an unrestricted scope
            // means the whole record stream — but one unit PER PLAY SESSION
            // rather than one spanning the file. A month-old log is mostly
            // nights, and a single unit across it hands "plat per hour" a
            // denominator made of sleep.
            foreach (var session in Presence().Spans)
            {
                units.Add(new ScopeUnit(session, null));
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
            or QuerySource.Experience or QuerySource.Faction or QuerySource.Loot
            or QuerySource.Considers)
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

    // ---- stances -----------------------------------------------------------

    /// <summary>One stance span clipped to one scope unit — the unit of stance uptime.</summary>
    private readonly record struct StanceInterval(TimeRange Range, string Stance);

    /// <summary>When the player was logged in; rebuilt only on new records.</summary>
    private PresenceTimeline Presence()
    {
        if (_presence is null || _presenceVersion != _records.Version)
        {
            _presence = PresenceTimeline.Build(_records);
            _presenceVersion = _records.Version;
        }

        return _presence;
    }

    /// <summary>Rebuilt only when records have been appended since the last build.</summary>
    private StanceTimeline Stances()
    {
        if (_stances is null || _stancesVersion != _records.Version)
        {
            _stances = StanceTimeline.Build(_records, _character);
            _stancesVersion = _records.Version;
        }

        return _stances;
    }

    /// <summary>
    /// Whether this query needs the stance clock wound at all. Most queries
    /// never mention stances, and building the intervals for them would be
    /// per-record work spent on a column nobody asked for.
    /// </summary>
    private static bool UsesStances(QuerySpec spec)
    {
        if (spec.GroupBy.Contains(Dimension.Stance))
        {
            return true;
        }

        foreach (var filter in spec.Filters)
        {
            if (filter.Dim == Dimension.Stance)
            {
                return true;
            }
        }

        foreach (var metric in spec.Metrics)
        {
            if (MetricCatalog.StanceMetrics.Contains(metric))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Clips the stance spans against each scope unit, laid out unit by unit so
    /// the record loop can walk them with a cursor instead of searching.
    /// <paramref name="unitFirst"/> holds the start offset of each unit's block
    /// (length units.Count + 1, the last entry being the total).
    /// </summary>
    private static List<StanceInterval> BuildStanceIntervals(
        StanceTimeline timeline, PresenceTimeline presence, List<ScopeUnit> units, int[] unitFirst)
    {
        var intervals = new List<StanceInterval>();
        for (var u = 0; u < units.Count; u++)
        {
            unitFirst[u] = intervals.Count;
            var range = units[u].Range;
            for (var s = timeline.FirstEndingAtOrAfter(range.Begin); s < timeline.Spans.Count; s++)
            {
                var span = timeline.Spans[s];
                if (span.Begin > range.End)
                {
                    break;
                }

                var clipped = new TimeRange(
                    span.Begin > range.Begin ? span.Begin : range.Begin,
                    span.End < range.End ? span.End : range.End);

                // And again against the play sessions. A stance is only ended
                // by the next switch, so one held at logout would otherwise be
                // "held" until the player next sat down — the overnight gap
                // counted as time in a stance nobody was standing in.
                foreach (var piece in presence.Intersect(clipped))
                {
                    intervals.Add(new StanceInterval(piece, span.Stance));
                }
            }
        }

        unitFirst[units.Count] = intervals.Count;
        return intervals;
    }

    /// <summary>
    /// True for records whose actor's stance this log actually knows: the owner
    /// and their pets. Everyone else's stance was written to THEIR log.
    /// </summary>
    private bool IsOwnerSide(string actor) =>
        string.Equals(actor, _character, StringComparison.OrdinalIgnoreCase) ||
        (_identity.OwnerOf(actor) is { } owner &&
         string.Equals(owner, _character, StringComparison.OrdinalIgnoreCase));

    // ---- aggregation -------------------------------------------------------

    private sealed class Node
    {
        public readonly CounterBag Bag = new();
        public readonly Dictionary<int, TimeRange> UnitSpans = [];

        /// <summary>Stance intervals this node saw a record in; null when stances are off.</summary>
        public HashSet<int>? StanceIntervals;
        public Dictionary<string, Node>? Children;
        public Dictionary<string, Node>? Actors; // pet-rollup drill-down
        public SortedDictionary<DateTime, double>? Buckets;
    }

    private QueryResult ExecuteCore(QuerySpec spec, int version)
    {
        var units = ResolveScope(spec.Scope, spec.Source);
        var root = new Node();

        var timeline = UsesStances(spec) ? Stances() : null;
        var unitFirst = new int[units.Count + 1];
        List<StanceInterval>? intervals =
            timeline is null ? null : BuildStanceIntervals(timeline, Presence(), units, unitFirst);

        for (var unitIndex = 0; unitIndex < units.Count; unitIndex++)
        {
            var unit = units[unitIndex];
            // Records inside a unit are time-ordered, so the matching stance
            // interval only ever moves forward.
            var cursor = unitFirst[unitIndex];
            var cursorEnd = timeline is null ? 0 : unitFirst[unitIndex + 1];
            for (var i = _records.LowerBound(unit.Range.Begin); i < _records.Count; i++)
            {
                var record = _records[i];
                if (record.Timestamp > unit.Range.End)
                {
                    break;
                }

                var stanceIndex = -1;
                if (intervals is not null)
                {
                    while (cursor < cursorEnd && intervals[cursor].Range.End < record.Timestamp)
                    {
                        cursor++;
                    }

                    if (cursor < cursorEnd && intervals[cursor].Range.Begin <= record.Timestamp)
                    {
                        stanceIndex = cursor;
                    }
                }

                Accumulate(spec, root, unit, unitIndex, record, intervals, stanceIndex);
            }
        }

        // Convert per-unit spans into merged active-time segments, bottom-up.
        SealActiveTime(root, intervals);

        var raidSeconds = root.Bag.ActiveTime.TotalSeconds;
        var metricNames = spec.Metrics.Count > 0 ? spec.Metrics : MetricCatalog.DefaultsFor(spec.Source);
        var scope = new MetricCatalog.MetricScope(
            raidSeconds, root.Bag.Total, root.Bag.StanceTime.TotalSeconds);

        var rows = EmitRows(root, metricNames, scope, spec);
        var totals = ComputeMetrics(root.Bag, metricNames, scope);
        return new QueryResult(rows, totals, raidSeconds, version);
    }

    private void Accumulate(
        QuerySpec spec,
        Node root,
        ScopeUnit unit,
        int unitIndex,
        TimedRecord record,
        List<StanceInterval>? intervals,
        int stanceIndex)
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

            case QuerySource.Loot:
                if (record.Event is not LootEvent lootEvent)
                {
                    return;
                }

                actor = lootEvent.Looter;
                break;

            case QuerySource.Considers:
                if (record.Event is not ConsiderEvent considerEvent)
                {
                    return;
                }

                actor = considerEvent.Target; // rows rank the conned targets
                break;

            default:
                return;
        }

        // The stance is a property of the moment, not of the record: resolve it
        // once here so grouping, filtering and the uptime clock all agree.
        // Only the owner's side carries a real stance — see IsOwnerSide.
        string? stance = null;
        var stanceSpan = -1;
        if (intervals is not null)
        {
            if (IsOwnerSide(actor))
            {
                stanceSpan = stanceIndex;
                stance = stanceIndex >= 0 ? intervals[stanceIndex].Stance : StanceTimeline.Unknown;
            }
            else
            {
                stance = StanceTimeline.NotTracked;
            }
        }

        foreach (var filter in spec.Filters)
        {
            if (!PassesFilter(filter, record.Event, actor, stance, spec))
            {
                return;
            }
        }

        // Walk the grouping levels, accumulating at every node (root = totals).
        // Pet rollup inserts an implicit actor level under merged player rows:
        // merged node carries the combined totals, actor nodes carry the split,
        // and deeper dimensions nest under the actors.
        AddToNode(root, record, damage, heal, unitIndex, stanceSpan, spec.BucketSeconds);

        var node = root;
        for (var level = 0; level < spec.GroupBy.Count; level++)
        {
            var dimension = spec.GroupBy[level];
            var key = DimensionKey(dimension, record.Event, actor, stance);
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

            AddToNode(child, record, damage, heal, unitIndex, stanceSpan, spec.BucketSeconds);
            node = child;

            if (rollup)
            {
                child.Actors ??= new Dictionary<string, Node>(StringComparer.Ordinal);
                if (!child.Actors.TryGetValue(actorName, out var actorNode))
                {
                    child.Actors[actorName] = actorNode = new Node();
                }

                AddToNode(actorNode, record, damage, heal, unitIndex, stanceSpan, spec.BucketSeconds);
                node = actorNode;
            }
        }
    }

    private static void AddToNode(
        Node node,
        TimedRecord record,
        DamageEvent? damage,
        HealEvent? heal,
        int unitIndex,
        int stanceSpan,
        int? bucketSeconds)
    {
        // The interval, not the record's instant: a stance held for a minute
        // counts the whole minute the moment anything happens inside it.
        if (stanceSpan >= 0)
        {
            (node.StanceIntervals ??= []).Add(stanceSpan);
        }

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
        else if (record.Event is LootEvent loot)
        {
            node.Bag.Add(loot);
        }
        else if (record.Event is ConsiderEvent consider)
        {
            node.Bag.Add(consider);
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
                : record.Event is LootEvent bucketLoot ? (bucketLoot.Copper ?? 0) / 1000.0
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

    private static void SealActiveTime(Node node, List<StanceInterval>? intervals)
    {
        foreach (var span in node.UnitSpans.Values)
        {
            node.Bag.ActiveTime.Add(span.Begin, span.End);
        }

        // Intervals arrive as a set of indices, so merging them here is what
        // turns "these stretches were touched" into a duration.
        if (intervals is not null && node.StanceIntervals is not null)
        {
            foreach (var index in node.StanceIntervals)
            {
                var range = intervals[index].Range;
                node.Bag.StanceTime.Add(range.Begin, range.End);
            }
        }

        if (node.Children is not null)
        {
            foreach (var child in node.Children.Values)
            {
                SealActiveTime(child, intervals);
            }
        }

        if (node.Actors is not null)
        {
            foreach (var actor in node.Actors.Values)
            {
                SealActiveTime(actor, intervals);
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

    private string DimensionKey(Dimension dimension, GameEvent evt, string actor, string? stance)
    {
        return dimension switch
        {
            Dimension.Player => actor,
            Dimension.Character => _character,
            Dimension.Stance => stance ?? StanceTimeline.Unknown,
            Dimension.Target => evt switch
            {
                DamageEvent d => d.Defender == actor ? d.Attacker ?? "Unknown" : d.Defender,
                HealEvent h => h.Target,
                DeathEvent de => de.Killer ?? "Unknown",
                LootEvent l => l.Source ?? "Unknown",
                _ => "Unknown",
            },
            Dimension.Spell => evt switch
            {
                DamageEvent d => d.SubType ?? DamageKindLabel(d.Kind),
                HealEvent h => h.Spell ?? "Unknown",
                CastEvent c => c.Spell ?? "Unknown",
                ExperienceEvent x => x.AaPoint ? "AA point" : x.Party ? "party" : "solo",
                FactionEvent f => f.Capped ? "capped" : f.Better ? "up" : "down",
                LootEvent l => l.Item ?? "coin",
                ConsiderEvent con => con.Attitude,
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

    private bool PassesFilter(QueryFilter filter, GameEvent evt, string actor, string? stance, QuerySpec spec)
    {
        bool matches;
        if (filter.Flag is { } flag)
        {
            matches = evt is DamageEvent damage && MatchesFlag(flag, damage);
        }
        else if (filter.Dim is { } dim && filter.Values is { Count: > 0 })
        {
            var key = DimensionKey(dim, evt, actor, stance);

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
        Node parent, IReadOnlyList<string> metricNames, MetricCatalog.MetricScope scope, QuerySpec spec)
    {
        if (parent.Children is null)
        {
            return [];
        }

        // Rank rows by the first requested metric — "total" for the combat
        // sources, but loots/xpPercent/factionNet for sources whose bags never
        // touch Total (everything would tie at 0 otherwise).
        var rankMetric = metricNames.Count > 0 ? metricNames[0] : "total";
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
                            ComputeMetrics(a.Value.Bag, metricNames, scope),
                            Nullable(EmitRows(a.Value, metricNames, scope, spec)),
                            EmitSeries(a.Value)))
                        .OrderByDescending(r => r.Metrics.GetValueOrDefault(rankMetric))
                        .ToList();
                }
                else
                {
                    // Single actor identical to the row: flatten the actor level.
                    children = Nullable(EmitRows(actors[key], metricNames, scope, spec));
                }
            }
            else
            {
                children = Nullable(EmitRows(node, metricNames, scope, spec));
            }

            rows.Add(new QueryRow(
                key, label,
                ComputeMetrics(node.Bag, metricNames, scope),
                children,
                EmitSeries(node)));
        }

        return rows.OrderByDescending(r => r.Metrics.GetValueOrDefault(rankMetric)).ToList();
    }

    private static List<QueryRow>? Nullable(List<QueryRow> rows) => rows.Count > 0 ? rows : null;

    private static IReadOnlyList<SeriesPoint>? EmitSeries(Node node) =>
        node.Buckets?.Select(b => new SeriesPoint(b.Key, b.Value)).ToList();

    private static Dictionary<string, double> ComputeMetrics(
        CounterBag bag, IReadOnlyList<string> metricNames, MetricCatalog.MetricScope scope)
    {
        var metrics = new Dictionary<string, double>(metricNames.Count, StringComparer.Ordinal);
        foreach (var name in metricNames)
        {
            metrics[name] = MetricCatalog.Compute(name, bag, scope);
        }

        return metrics;
    }
}
