# ADR-004: Query engine

Status: accepted (2026-08-01). Scope: phase 4 — QuerySpec execution, the metric
catalog, canned specs.

## Design

- **`QuerySpec`** is the serializable aggregation description from the system
  overview: source × scope × groupBy × metrics × filters × bucketing × pet
  rollup. `QuerySpecJson` fixes the canonical camelCase JSON shape used by saved
  queries, dashboards, and the API. The canned classic views
  (`CannedQueries`) are ordinary specs — feature F4's "editable in the builder"
  guarantee falls out for free.
- **Scope resolution**: fight selections resolve to per-fight ranges
  [begin, lastDamage]; damage rows are records whose *defender is that fight's
  NPC*, tanking rows those whose *attacker is* — the fight key does the
  side classification, exactly like the incumbent's per-fight damage blocks.
  Healing/casts/deaths aggregate over the selection's *merged* time ranges
  instead (healers heal between pulls; overlapping fights can't double-count).
  Explicit time-range scopes fall back to identity-based side resolution.
  Trims (skip-first/max-seconds) apply to the merged virtual timeline and are
  re-intersected with fight ranges.
- **`TimeSegments`** implements the §4 denominators: inclusive [begin, end]
  second ranges (verified against the reference: `end − begin + 1`), merged on
  overlap/adjacency. Active time = per-row union of first-to-last action per
  scope unit; raid time = the root bag's union over everyone.
- **`CounterBag`** holds the §2 monotone counters; orientation-agnostic (on the
  damage side "Dodges" means my swings got dodged, on the tanking side "I
  dodged" — the row key orients). Regular-melee set for flurry denominators
  matches the reference ({Bites, Claws, Crushes, Pierces, Punches, Slashes} +
  "Hits"). **`MetricCatalog`** implements §5 once, shared everywhere; notable
  fidelity points: avg crit excludes lucky hits, lucky rate is conditional on
  crits, twincast rate doubles DD twincasts but not DoT ticks (capped at 100).
- **Grouping** builds a node tree per groupBy level; pet rollup inserts an
  implicit actor level under merged player rows ("Owner +Pets" label, actor
  breakdown as the first drill, deeper dimensions under each actor; single-actor
  rows flatten the level away). Rollup uses the identity registry at query time,
  so a late pet mapping fixes history with no reparse.
- **Validity toggles are filters** (§7): damage-shield/headshot/assassinate/
  finishing-blow/slay-undead match on record flags at query time. `bane` is in
  the model but matches nothing until spell-DB classification lands (the
  reference derives bane from spell data, not line text).
- **Caching**: results keyed by serialized spec, invalidated by
  records-version + fights-version. Same spec, same data → same instance.

## Deviations

- Metrics are unrounded doubles; the reference rounds DPS to integers at the
  model layer. Formatting (K/M/B, decimals) is presentation, applied in the UI.
- AoE-heal/swarm-pet dedup (§8's ~7 s window) and resist-rate metrics are
  deferred; both slot in as record-level filters/counters without model changes.
- Bucketed series carry landed totals per bucket; charts derive per-second DPS
  from them.

## Verification

`QueryEngineTests` fixes one scenario computed on paper (in the test header) and
asserts exact metric values for damage/tanking/healing summaries: DPS vs SDPS
denominators (625/7 vs 100/2 vs 100/7), crit rates with and without a DS record,
avg-crit lucky exclusion, overheal rate, undefended rate including a dodge,
damage-type grouping by school, fight-scoped totals, trim windows, per-second
series, cache identity, and spec JSON round-trips.
