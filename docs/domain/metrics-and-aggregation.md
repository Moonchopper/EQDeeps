# Metrics, Fights, and Aggregation — Domain Reference

How parsed records become fights, and fights become the numbers players argue about. Extracted from the reference implementation (EQLogParser: `FightManager`, `StatsUtil` — the formula authority — and the stats builders). **Match these semantics unless a deviation is deliberate and documented**; raiders compare parses across tools, and numbers that disagree with the incumbent will be read as bugs.

## 1. Fight segmentation

- A **fight** is keyed by NPC name and created lazily on the first *valid* combat record between a player-side entity and that NPC (either direction).
- Validity: exactly one side must be an NPC. Player↔player records (duels, DS between players) and NPC↔NPC records don't create fights. Identity rules are in `eq-log-format.md` §5; also track spells recently cast by players (~5 min window) so "spell as attacker" lines attribute to the caster's side.
- A fight **ends** when: the NPC dies (`died.`/`slain by` — mark dead, close immediately), or no combat activity references it for **30 seconds** (60 s hard cap regardless of trailing non-damage activity). Same NPC name pulled again later = a **new** fight (never merge).
- Fights record for each side: begin/last damage times, running totals and hit counts (damage dealt to the NPC = "damage"; damage dealt by the NPC = "tanking"), taunt events, and per-second buckets (§3).
- **Inactivity grouping:** consecutive fights whose gaps are below a threshold group into a "pull chain"; a gap ≥ the group timeout (**120 s** in the reference) inserts a break ("Break Time" divider in the reference UI). Groups let users select "that whole event" in one click.
- **Player-turned-NPC correction:** if a fight's key later becomes a verified player, delete the fight (it was a misclassification).
- **Selection semantics:** the user selects fights (or groups); analysis scope = the union of the selected fights' time ranges. All summary stats are computed over that union.

## 2. The raw counters (per player, and per player-per-subtype)

Aggregation happens on ~40 monotone counters per entity; every derived metric is a pure function of them. Track at minimum, for each player (and each subtype breakdown — spell name or melee skill — under them):

- Totals: `Total` (landed), `Extra` (overheal or absorbed-over amount), `MaxHit`, `MinHit`, `MaxPotentialHit`
- Hit counts: `Hits`, `CritHits`, `LuckyHits`, `TwincastHits`, `NonTwincastCritHits`, `NonTwincastLuckyHits`
- Melee detail: `MeleeAttempts`, `MeleeHits`, `RegularMeleeHits` (excludes kick/bash/etc. specials), `BowHits`, `DoubleBowHits`, `FlurryHits`, `RampageHits`, `RiposteHits`, `StrikethroughHits`
- Specials: `BaneHits`, `AssassinateHits` (`AssHits`), `HeadshotHits`, `FinishingBlowHits`, `SlayUndeadHits`
- Defense (tanking side): `Misses`, `Dodges`, `Parries`, `Blocks`, `Absorbs`, `Invulnerable`
- Spell: `SpellHits`, resist counts (per spell)
- Histograms: hit-size frequency, split crit vs non-crit (for hit-distribution views)
- Time: per-entity active `TimeSegments` (see §4)

## 3. Time bucketing

Log resolution is 1 second, so the canonical timeline is **per-second buckets** of records. Everything else (N-second bucketing for charts, rolling windows, whole-fight totals) aggregates buckets. Recommended invariant: fights store per-second buckets; queries never re-scan raw lines.

- **DPS time series:** per-second landed totals; also cumulative fight totals for "average DPS so far" curves.
- **Rolling DPS:** 5-second sliding window (sum of last 5 seconds ÷ 5) — the standard "current burst" number.
- **Gaps:** when charting across a selection with dead time (between pulls), break the line rather than drawing across the gap.

## 4. Active time vs raid time — the DPS denominators

The most disputed numbers in parsing come down to denominators. The reference semantics:

- A player's **active time** = union of their damage `TimeSegments` across the selected fights: first-to-last action per fight, merged across overlapping fights. Standing around between pulls does not count against personal DPS.
- **Raid time** = union of *everyone's* segments over the selection (total encounter duration).
- Time segments are unions of `[begin, end]` ranges with overlap merging — a fight selection where two fights overlap in wall-clock time must not double-count seconds.
- A **min/max time trim** may be applied to the selection ("only the first 60 s of the fight", "skip the first 10 s") before computing anything.
- **Presence** bounds all of the above. A whole-log scope resolves to one unit PER PLAY SESSION rather than one spanning the file (see [log format §3.13](eq-log-format.md)), so `platPerHour` and `xpPerHour` divide by time played rather than by the calendar. On a real 998-hour log holding 53.7 hours of play, that is the difference between 23.3 and 1.3 plat an hour. Stance spans are intersected with presence for the same reason: a stance is only ended by the next switch, so one held at logout would otherwise be "held" until the player next sat down.
- **Stance time** is a third denominator, used only by the stance breakdown: the union of the stance's spans clipped to the scope. It counts every second the stance was *held*, including the ones that produced nothing. Active time would refund exactly those seconds, which is the cost a slower stance is being weighed for — so `stanceDps = total ÷ stance time` is the fair comparison and plain `dps` is shown beside it rather than instead of it. A stance span with no records in scope is not counted (it has no row either).

## 5. Derived metric catalog (formulas)

For each player (and each subtype under them):

| Metric | Formula | Notes |
|---|---|---|
| DPS | `Total / activeSeconds` | personal denominator |
| SDPS | `Total / raidSeconds` | "shared DPS" — comparable across players; the ranking metric |
| PDPS / Potential | `(Total + Extra) / activeSeconds` | includes overheal/absorbed portion |
| Avg hit | `Total / Hits` | |
| Avg crit | `critTotal / (CritHits − LuckyHits)` | **lucky hits are excluded from crit average** (tracked separately) |
| Avg lucky | `luckyTotal / LuckyHits` | |
| Avg non-twincast | non-TC total / non-TC hits | ditto for non-TC crit / non-TC lucky variants |
| Crit rate % | `CritHits / Hits × 100` | |
| Lucky rate % | `LuckyHits / CritHits × 100` | lucky is conditional on crit |
| Twincast rate % | DD: `TwincastHits × 2 / Hits × 100` capped at 100; DoT: `TwincastHits / Hits × 100` | the ×2 reflects that a twincast produces the extra hit itself |
| Flurry rate % | `FlurryHits / RegularMeleeHits × 100` | denominator excludes special attacks |
| Rampage rate % | `RampageHits / MeleeHits × 100` | |
| Riposte rate % | `RiposteHits / MeleeHits × 100` | |
| Double-bow rate % | `DoubleBowHits / BowHits × 100` | |
| Strikethrough rate % | `StrikethroughHits / MeleeHits × 100` | |
| Melee hit rate % | `MeleeHits / MeleeAttempts × 100` | |
| Melee accuracy % | `MeleeHits / (MeleeAttempts − Parries − Dodges − Blocks − Invulnerable − Absorbs) × 100` | removes defender-skill outcomes from the denominator |
| Undefended % | share of attempts not avoided | tanking view; and F26's incoming-damage rates |
| Overheal % (`ExtraRate`) | `Extra / (Total + Extra) × 100` | healing views |
| Resist rate % | `resists / (SpellHits + resists) × 100` | per spell |
| % of total | `playerTotal / grandTotal × 100` | within current scope |
| Per second held (`stanceDps`) | `Total / stanceSeconds` | stance breakdown; see §4 for why not `activeSeconds` |
| Time held (`stanceSeconds`) | union of the stance's spans ∩ scope | rendered as a duration, not a count |
| Uptime % (`stanceUptime`) | `stanceSeconds / scope stanceSeconds × 100` | share of *tracked stance time*, so the column sums to 100% |

Formatting conventions: large numbers abbreviate K/M/B with one decimal (`126.2K`, `1.6M`); rates one decimal place. The "parse to chat" text format ranks by SDPS (damage) or total (healing/tanking).

**Every avoidance denominator is short by the ripostes.** A swing the defender riposted is written as the defender's own counter-attack line and the attempt records nothing at all ([log format §3](eq-log-format.md)), so `MeleeAttempts` counts the swings the *log accounted for*, not the swings thrown. This affects melee hit rate, melee accuracy, undefended % and F26's incoming rates alike. It is a property of what EverQuest writes down, not a parser choice, and the honest phrasing everywhere is "of the swings the log accounted for".

## 6. Pet attribution

- Every damage/heal record carries optional `owner` when the actor is a mapped pet.
- Summaries roll pets into `"<Owner> +Pets"` rows; the row's children (owner alone, each pet) remain drillable. Time segments for the merged row are the union of owner + pet segments.
- Unmapped pets aggregate under "Unknown Pet Owner" until a mapping event (pet-leader line, user override) retroactively reassigns — mappings apply at query time, so late mappings fix history without reparse.
- The same rollup applies in time-series (pet damage merges into owner's series) and in the live meter.

## 7. Damage-validity toggles → query-time filters

Classic parse etiquette lets users exclude categories: **bane** damage, **damage shields**, **headshot / assassinate / finishing blow / slay undead** (one-shot mechanics that distort comparisons). The reference applies these at *ingest*, forcing recomputes on change — a known flaw. **EQDeeps requirement: these are flags on each record, and exclusion is a query-time filter.** Same for melee-only / spell-only splits (tanking views) and AoE-heal / swarm-pet-heal dedup rules below.

## 8. Healing specifics

- Heals are not tied to fights (healers heal between pulls, out of combat); healing summaries slice the heal record stream by the *selected time ranges* rather than by fight membership.
- Overheal per record = `potential − landed` (see log-format §3.7); track both.
- HoT ticks vs direct heals are distinct subtypes (different denominators for "heal count" stats).
- **AoE heal dedup:** group heals (splashes, wards) generate one line per recipient; the reference validates AoE heals within a ~7-second window per spell to avoid double-counting swarm-pet recipients and duplicate procs — a heal to a swarm pet can be excluded by toggle.
- Healing views come in two orientations: healer-centric (who healed how much, by spell, to whom) and target-centric (who *received* healing, from whom) — the tanking summary attaches received-healing to each tank.

## 9. Death recap

For each player death: take the **preceding ~20 seconds** and interleave, in timestamp order: incoming damage records (attacker, skill/spell, amount, modifiers), heals received (healer, spell, landed/overheal), and buffs/debuffs landing (received-spell records). Present with per-second grouping and a running "damage in last N seconds" context. The death record itself carries victim and killer. (Also surface a per-fight death count and a "deaths" marker lane on time-series charts.)

## 10. Aggregation architecture guidance (soft)

Derived from the reference but adapted for the query-model product vision:

- Store **immutable parsed records** (damage/heal/cast/death/…) in a time-indexed store per session; fights hold per-second bucket references. This is the query engine's source of truth.
- All summary computation should be **pure functions over (records ∩ time-ranges ∩ filters)** returning counter bags (§2) then derived metrics (§5). No cached stat may survive a filter change unnoticed — cache keyed by (query, scope, data version).
- The live path is the same computation incrementalized: per-second tick updates to the current fight's counters, pushed to subscribed panels. The reference's separate "overlay stats builder" (recompute-on-1s-tick over active fights) is a simple, proven fallback shape if incrementalizing proves fiddly.
- Expect **out-of-order-free but duplicate-prone** input: timestamps never interleave across a single file read, but rotation/reopen and re-backfill can replay lines; idempotency (line-position watermarks) beats dedup heuristics.
