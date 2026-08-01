# ADR-001: Parser core design

Status: accepted (2026-07-31). Scope: phase 1 of the build order — pure line→record parsing.

## Decisions

1. **Pure, keyword-anchored string parsing; no regex.** Every grammar is an ordinal
   `IndexOf`/`StartsWith` scan (see domain doc §7: hostile input, bounded work,
   no throw-on-malformed). Records are emitted as immutable C# records under
   `EQDeeps.Core.Events`; parsing is `string → GameEvent?` with no I/O.
2. **Instance-based dispatcher, static grammar functions.** `LogEventParser` owns
   per-session mutable state (currently only the one-line EMU crit lookbehind in
   `DamageParser.State`); the grammar families themselves are static pure functions.
   No process-global state anywhere — multi-character monitoring is a constructor call.
3. **Chat first, always terminal.** `ChatParser` runs before combat grammars; the
   earliest channel-clause match in a line wins, which makes quoted player text unable
   to reach the combat grammars.
4. **Timestamps live in ingestion, not parsing.** `LogTimestamp`/`LogLineSplitter`
   parse the fixed-width 27-char prefix and split the "two entries on one physical
   line" glitch by probing for a strictly valid embedded timestamp. Strict shape
   validation (day/month names, digit positions, calendar range) prevents false splits
   on bracketed chat text such as /who output.
5. **`recognized` out-flag instead of throwing.** Unmatched lines return null with
   `recognized == false`; ingestion will count them (unmatched-rate is a release gate,
   measured on real logs in a later phase).
6. **Representation deviations from the reference** (information-preserving, mapped in
   fixtures): unknown actors are `null` (reference: `"Unknown"` label); generic
   subtypes are `null` (reference: type-name label strings); avoidance outcomes are
   `DamageKind` values (reference: type label strings); `Wild Rampage` is a separate
   flag from `Rampage` (rampage-rate denominators must count both).
7. **Preserved reference semantics** that are easy to get wrong: `(Riposte
   Strikethrough)` on a hit drops the riposte flag (strikethrough-dominant); a
   defender's successful riposte line produces no record at all; lucky/crit modifier
   coupling is deferred to the metrics layer; slain lines are deaths only, never
   damage; cross-server names are `Server.Name` and reduce to the post-dot segment
   (domain doc corrected accordingly).

## Rejected alternatives

- **Source-generated regex grammars** — measurable but unnecessary; keyword scans are
  simpler to bound and to fuzz, and the ingestion brief pushes perf work to a later,
  benchmarked phase. Revisit only with benchmarks in hand.
- **Single grand tokenizer** — the grammars are too irregular (names contain commas,
  verbs, backticks); per-family anchored scans match the domain doc structure and the
  fixture corpus one-to-one.

## Verification

`tests/EQDeeps.Core.Tests` runs a JSON fixture corpus (~190 cases) harvested from
EQLogParser's parser tests (expected values as data — see NOTICE) plus the domain-doc
examples: damage/avoidance/absorbs, DoT orderings (live + EMU), damage shields, heals
with overheal notation, deaths, casts, chat channels, taunts, zone, resists, modifier
masks, timestamp/splitter edge cases.
