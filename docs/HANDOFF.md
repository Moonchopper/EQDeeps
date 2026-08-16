# EQDeeps — Implementation Handoff

> **Status (2026-08-02): all eight build-order phases below are complete** —
> parser core, ingestion, session state, query engine, API + live loop, SPA,
> composable dashboards, packaging — each with its exit criteria verified in
> tests and its decisions recorded in `docs/architecture/adr-001…014`. Since
> then: a WebView2 windowed shell (ADR-009), an event timeline, a bundled
> sample log, Azure Artifact Signing for releases
> (`docs/release-signing.md`), consent-driven auto-updating shipped as an
> Inno Setup installer alongside the portable zip (ADR-010, feature F22), and
> estimated mob health, measured from damage-to-death and keyed by the
> instance difficulty read off the zone line (ADR-012, feature F25), and
> incoming damage — a raw ordered feed of swings taken plus a learned attack
> profile keyed on the defender's level as well as the mob (ADR-013, feature
> F26), and navigation reorganized into a single left rail with the fight list
> hidden on the one view that has no time frame to select (ADR-014), and zone
> maps — the player's own map files read from their EverQuest install, drawn
> with pan and zoom, plus the world as a routable graph built from the maps'
> own connection labels (ADR-016, feature F27; the format is documented in
> `docs/domain/eq-map-format.md`, which is the only domain doc with no
> reference implementation behind it), now with a player-chosen era filter so a
> classic-era server is not routed through Planes of Power (issue #57; the
> zone-id bands behind it are in that doc's §5.3 and re-derivable with
> `scripts/derive-zone-eras.mjs`), and the rail regrouped by the question a
> view answers — Combat, Character, World, Dashboards — with the group
> deciding whether the fight list and time controls apply, and the set-once
> preferences gathered into a Settings dialog and the log picker into a Logs
> dialog, both reached from a utility cluster at the foot of the rail, and
> the rail given icons so it can collapse to them — and does, on the Map
> (ADR-017, all six decisions shipped); and the linked highlight gained a
> click-to-select and a pin (`highlight.tsx`: a selection is a hover that
> stays — on this view, or pinned, on every view and across restarts, with a
> chip in the header saying which); and the first slice of reference lookup —
> an arrow beside every item and mob name (fight list, Summary, Incoming,
> Deaths, Loot, Mobs) that opens the community sites for the log's world
> (Legends: EQL Wiki, Gnoll Guard, EQLBase; guessed from the install,
> overridable in Settings), the design and the discovery behind it in ADR-019
> and `docs/domain/eq-client-files.md` (the client ships no item or NPC
> database; Legends logs carry no item-link ids; names are the join key);
> and a log cache so a log is parsed once — its records are written to disk
> after backfill and the next open restores them and resumes at the byte
> where the last one stopped, ~3× faster, with every repeating string pooled
> so the session holds half the memory it did, and the World view's map
> labels kept the same way (ADR-018, feature F28, issue #59); and the second
> slice of reference lookup — a per-server item registry fed by the log and
> by the player's own client files (`userdata\LF_*.ini`, the inventory dump),
> which numbers the items so the id-addressed sites light up, and an Item
> feed at the top of the Loot view listing every item looted, sold, bought or
> named in chat, newest first, with a door on each (F29; also fixed the loot
> grammar dropping `an` and stack counts, and parsed merchant sales).
> and a Bestiary: every mob the game has, searchable, with its listed level,
> health, loot and spawns fetched from EQLBase on demand and cached here —
> never bundled, never fetched until asked, switchable off — shown beside what
> your own logs measured for the same mob (ADR-020, feature F30, issue #51),
> and the spell emotes a buff prints when it lands or fades resolved against
> the player's own `spells_us*.txt`, read from their install — 94k lines of the
> owner's log that used to go past unrecognized, now events, with a spell
> named only when the text belongs to one (F10a).
> Currently at **v0.13.2**. See `docs/product/features.md` for per-feature
> status. The main open items: the release-gate invariants (CLAUDE.md §8 —
> defined, not yet written), class detection from the client's spell files
> (the emote and duration halves shipped as F10a/F10b), identity-registry disk
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
7. `domain/eq-legends-loadouts.md` — EQ Legends lets one character carry several class loadouts, each levelling independently, and logs nothing when they swap. Short, and it has already caused two bugs.

## The reference implementation

`d:\git\EQLogParser` (also `github.com/kauffman12/EQLogParser`, Apache 2.0) is the incumbent this project succeeds. Ground rules:

- **Behavior authority, not code source.** When a grammar or formula question isn't answered by the domain docs, read the reference to determine *behavior*, then update the domain doc. **Do not port/transcribe its code** — this is a clean-room rewrite (different architecture, and we keep licensing simple).
- Its parser tests (`EQLogParser.Wpf.Test/src/parsing/*.cs`) hold hundreds of real log lines with expected parse results. The fixture corpus was harvested from them and **is attributed in `NOTICE`** — that attribution is a licence obligation and stays as long as the fixtures do.
- Its `data/*.txt` files (spell DB, NPC names, pet names) were never copied, and are no longer wanted: the game client ships its own complete spell database and the app reads it from the player's install (F10a/F10b, `docs/domain/eq-client-files.md`). Reading the player's own files beats copying anyone's.
- EQDeeps is MIT. `NOTICE` records both the fixture attribution and the fact that no source code was ported.

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
- Release-gate invariants (CLAUDE.md §8): per-player damage sums to the fight total, every damage record lands in exactly one fight or none, recognized plus unrecognized equals lines read. Defined, not yet written. Comparing against EQLogParser was retired on 2026-08-16 — it parses live EverQuest, this app is used on Legends, and the two disagree by design on denominators.

## Visual language (ADR-015)

The SPA was re-themed in August 2026 to a rounded dark language: chrome on
`--page` and panels on `--surface` (a 1.216:1 step, which is near the ceiling —
the WCAG formula caps dark-on-dark), three opaque rule tiers replacing a single
alpha, IBM Plex Sans bundled at 45.7 KB, and row meters as rounded pills drawn
by a pseudo-element rather than a row background.

Two things to know before touching it:

- **`ui/src/chartTheme.ts` is where ECharts is told what the app looks like.**
  It builds itself by reading the CSS custom properties, so `styles.css` stays
  the single source of truth. Before it existed the chart layer held 66 colour
  literals and no root `textStyle`, so every chart drew its labels in a
  different typeface from the DOM.
- **Eight is the ceiling for chart colours on a dark ground**, and sixteen
  mutually separable fills do not exist at any level of care — the arithmetic
  is in ADR-015. Slots 1–8 are gated on all pairs, 9–16 on adjacency, and they
  are reached only by table rows where the name labels the chip.

`npm --prefix ui run test:layout` renders the dense surfaces and asserts
geometry; it runs in CI. Every check in it has been confirmed to fail before
being trusted, which is worth preserving — three checks passed over broken
output during the re-theme by asserting the easy adjacent property rather than
the one that carried the meaning.

## Working agreements

- Update the domain docs when reality disagrees with them — they are the spec of record.
- Record significant design choices as short ADRs under `docs/architecture/`.
- Any dependency added: check license first (MIT/Apache-2.0/BSD only), note it in NOTICE if it ships.
- Repo: `Moonchopper/EQDeeps` (private for now; public release is the goal — write code and history as if public).

## Acid test (docs completeness)

A fresh reader of only this `docs/` tree should be able to: (a) state what to build first and how to verify it; (b) correctly parse `Sonozen hit Jortreva the Crusader for 38948 points of fire damage by Burst of Flames. (Lucky Critical Twincast)` into a typed record; (c) hand-compute DPS, SDPS, and crit rate for a toy two-player fight from the formula catalog. If any of those fail, the docs need fixing — tell the owner.
