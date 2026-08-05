# EQDeeps — Implementation Handoff

> **Status (2026-08-02): all eight build-order phases below are complete** —
> parser core, ingestion, session state, query engine, API + live loop, SPA,
> composable dashboards, packaging — each with its exit criteria verified in
> tests and its decisions recorded in `docs/architecture/adr-001…011`. Since
> then: a WebView2 windowed shell (ADR-009), an event timeline, a bundled
> sample log, Azure Artifact Signing for releases
> (`docs/release-signing.md`), consent-driven auto-updating shipped as an
> Inno Setup installer alongside the portable zip (ADR-010, feature F22), and
> gear snapshots read from `/outputfile inventory` (ADR-011, feature F24 —
> the first thing the app persists that it cannot recompute from the log).
> Currently at **v0.9.0**. See `docs/product/features.md` for per-feature
> status. The main open items: real-log validation against EQLogParser (the
> release gate), the spell-DB work (class detection, bane, lands-on
> resolution — reference data files not yet copied), identity-registry disk
> persistence, and the P1/P2 backlog.
>
> **Releasing:** tags are single-use (GitHub immutable releases reserve a tag
> name permanently, even after its release is deleted), so run the
> **Verify signing key** workflow before tagging. Details in
> `docs/release-signing.md`.

You are picking up a **documented, greenfield** project: a clean-room, modern successor to EQLogParser. Everything you need to know about *what* to build and *what the data means* is in these docs; *how* to build it is largely yours, within the locked decisions.

## Read in this order

1. `product/vision.md` — what this is, who it's for, the three UX pillars, non-goals.
2. `product/features.md` — P0/P1/P2 features with acceptance criteria.
3. `domain/eq-log-format.md` — the log-line taxonomy with real examples. **The crown jewels; read carefully.**
4. `domain/metrics-and-aggregation.md` — fights, counters, formulas, denominators.
5. `architecture/system-overview.md` — locked stack, component boundaries, the QuerySpec model.
6. `architecture/log-ingestion-brief.md` — fresh-design mandate for file reading.
7. `domain/inventory-file-format.md` — the `/outputfile inventory` dump (F24). Only needed for gear; unlike the log format it has no prior art in the reference implementation.

## The reference implementation

`d:\git\EQLogParser` (also `github.com/kauffman12/EQLogParser`, Apache 2.0) is the incumbent this project succeeds. Ground rules:

- **Behavior authority, not code source.** When a grammar or formula question isn't answered by the domain docs, read the reference to determine *behavior*, then update the domain doc. **Do not port/transcribe its code** — this is a clean-room rewrite (different architecture, and we keep licensing simple).
- Its parser tests (`EQLogParser.Wpf.Test/src/parsing/*.cs`) hold hundreds of real log lines with expected parse results — **harvest these as fixture data** (log lines are game output, not creative code; copying expected-value assertions as data is fine).
- Its `data/*.txt` files (spell DB, NPC names, pet names — see domain doc §6) can be copied outright; they're Apache 2.0 — **add attribution to a NOTICE file** ("Reference data derived from EQLogParser, © kauffman12, Apache License 2.0").
- EQDeeps itself: choose a license at public release (owner's call; MIT or Apache 2.0 both fine given the above).

## Suggested build order

Phases, each independently verifiable (adjust with reason; keep checkpoints):

1. **Parser core.** Pure line→record functions + the fixture corpus as tests. Exit: every fixture parses to expected values; unmatched-line rate on a real log sample is measured and logged, not thrown.
2. **Ingestion.** Per the brief, with replay harness + benchmarks. Exit: brief's verification list green.
3. **Session state.** Fights, record store, identity registry. Exit: replaying a synthetic raid log yields the expected fight list and per-player counters (hand-computed fixtures).
4. **Query engine.** QuerySpec execution + canned specs. Exit: damage/healing/tanking summaries for fixtures match hand-computed metric values; validity toggles work as filters without reparse.
5. **API + live loop.** Sessions REST + realtime channel + live meter tick. Exit: `scripts`-driven append to a temp log shows sub-250 ms update in a test client.
6. **SPA MVP.** Fight list, summaries, DPS chart, default dashboard. Exit: features F1–F7 acceptance criteria.
7. **Composable queries + dashboards UI.** F6/F8.
8. **Packaging.** F14.

## Verification without EverQuest

There is no EverQuest install in the loop. The whole product is testable by **writing lines to files**:

- Build a **synthetic log generator** early (phase 2): emits realistic raid scenarios (N players, pets, heals, deaths, chat noise, timestamp pacing, the two-entries-on-one-line glitch, truncation/rotation events). It powers unit fixtures, benchmarks, and end-to-end tests.
- Live-mode e2e: start the app on a temp `eqlog_Test_server.txt`, append lines on a schedule, assert UI/API updates.
- Real-log validation: the owner has real logs; before release, compare summary numbers for a real fight against EQLogParser's output for the same file (they should agree within documented deviations).

## Working agreements

- Update the domain docs when reality disagrees with them — they are the spec of record.
- Record significant design choices as short ADRs under `docs/architecture/`.
- Any dependency added: check license first (MIT/Apache-2.0/BSD only), note it in NOTICE if it ships.
- Repo: `Moonchopper/EQDeeps` (private for now; public release is the goal — write code and history as if public).

## Acid test (docs completeness)

A fresh reader of only this `docs/` tree should be able to: (a) state what to build first and how to verify it; (b) correctly parse `Sonozen hit Jortreva the Crusader for 38948 points of fire damage by Burst of Flames. (Lucky Critical Twincast)` into a typed record; (c) hand-compute DPS, SDPS, and crit rate for a toy two-player fight from the formula catalog. If any of those fail, the docs need fixing — tell the owner.
