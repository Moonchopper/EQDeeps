# EQDeeps — System Overview (Architecture Guidance)

Status: **guidance, now largely implemented.** The stack and component
boundaries below are built as described; the open choices this doc left have
been resolved and recorded in ADRs: realtime = SignalR (ADR-005), ingestion
design (ADR-002), fight/identity semantics (ADR-003), query engine + caching
(ADR-004), dashboards/persistence (ADR-007), packaging (ADR-008). Known gap
vs. this doc: the identity registry is per-server and snapshot-serializable
but not yet written to disk; reference data files (spells/npcs/petnames) are
not yet shipped.

## Locked decisions

- **Backend:** .NET 8 (C#), ASP.NET Core. Runs locally on the player's Windows machine. Cross-platform-clean core where it's cheap (no gratuitous Windows APIs outside packaging).
- **Frontend:** web SPA — React + TypeScript (Vite), served by the backend; real-time via SignalR or raw WebSockets (choose and document).
- **Charting/layout:** permissive licenses only (e.g., Apache ECharts, or equivalent canvas-based lib that handles per-second series at raid scale; react-grid-layout or similar MIT lib for dashboards). Validate license before adopting anything.
- **Multi-character concurrent monitoring from day one** — no process-global parser state; everything session-scoped.
- **Clean-room:** do not port code from the reference implementation. Behavior fidelity comes from `docs/domain/` and fixtures, not from transcribing its source.

## Component sketch

```
                    ┌──────────────────────────────────────────────────┐
                    │                EQDeeps backend (.NET 8)          │
 eqlog_A_serv.txt ─▶│ Ingestion ─▶ Parser ─▶ Session state             │
 eqlog_B_serv.txt ─▶│  (per-file)  (grammar)  ├─ record store (time-idx)│
                    │                         ├─ fight index            │
                    │                         └─ identity registry      │
                    │                              │        (per server)│
                    │        Query engine ◀────────┘                   │
                    │         │    │                                    │
                    │      REST   realtime push (SignalR/WS)            │
                    └─────────┼──────┼────────────────────────────────┘
                              ▼      ▼
                    React SPA: dashboards of panels
                    panel = saved QuerySpec + visualization type
```

- **Ingestion** (see `log-ingestion-brief.md` — fresh design mandated): one pipeline per opened file; emits timestamped lines with backfill/live phase marking.
- **Parser**: pure, instance-based (no statics), line → typed record (DamageRecord, HealRecord, CastRecord, DeathRecord, …) per the grammars in `docs/domain/eq-log-format.md`. Must be testable as a pure function: `string → record?`.
- **Session state**: a `Session` per opened log owns its ingestion, parser, fight index, and record store. The **identity registry** (players/pets/classes) is shared *per game server* across sessions on that server (characters on one server see the same world), persisted.
- **Query engine**: executes QuerySpecs (below) against session state; caches keyed by (spec, scope, data-version).
- **API layer**: REST for request/response (sessions, fights, query execution, saved dashboards); realtime channel for pushes (fight list deltas, live panel ticks). Bind to localhost only.

## The query model (heart of the product)

A **QuerySpec** is a serializable description of an aggregation:

```jsonc
{
  "source": "damage" | "healing" | "tanking" | "casts" | "deaths" | ...,
  "scope":  { "sessions": ["A"], "fights": [123, 124] | "selection" | "live" ,
              "trim": { "skipFirstSec": 0, "maxSec": null } },
  "groupBy": ["player"] ,          // dimensions: player, spell, target, character,
                                    // class, damageType, modifier, second-bucket...
  "metrics": ["total", "sdps", "critRate"],   // from the metric catalog
  "filters": [ { "dim": "class", "in": ["Necromancer"] },
               { "flag": "baneDamage", "exclude": true } ],
  "bucket":  { "seconds": 1 } | null,          // null = whole-scope aggregate
  "petRollup": true
}
```

Rules:
- Metric formulas and denominators come from `docs/domain/metrics-and-aggregation.md` §5 — implement once, in one place, shared by tables/charts/live meter.
- Validity toggles (bane/DS/headshot/…) are **filters**, never ingest-time drops.
- Canned specs ship for the classic views (damage/healing/tanking summary, DPS-over-time, deaths); the UI's "edit view" opens the same spec in the builder. Saved specs are named and reusable across panels.
- `scope: "live"` subscribes the panel to the realtime channel; the engine pushes incremental results (per-second ticks for the active fights).

## Realtime expectations

- Target file-append → visible update **≤ 250 ms** (reference app: 1–2 s).
- Push granularity: per-second batches are fine (log resolution is 1 s); don't push per-line.
- Backfill and live phases must be distinguishable so the UI can show progress and panels can defer recompute until backfill completes (then go incremental).
- Design the line stream as subscribable internally (a future trigger system attaches here — non-goal now, don't preclude).

## Persistence

Root: `%AppData%\EQDeeps\` (path-provider abstraction; no hardcoding scattered around).

| What | Suggestion |
|---|---|
| App settings | JSON file |
| Dashboards / saved queries | JSON files (exportable/importable by design) |
| Identity registry (players/pets/classes per server) | JSON or SQLite — small, write-debounced |
| Reference data (spells/npcs/petnames…) | shipped read-only alongside the app; see domain doc §6 and NOTICE obligations |
| Parsed records | in-memory per session, with a per-log binary cache under `cache\` (`<hash of path>-<parser build>.eqdc`) written after backfill and once a minute after; the next open restores the records and resumes the parser at the cached byte offset. Records only — fights and identity are replayed — stamped with the parser build and validated against the log's own bytes (ADR-018) |
| Learned mob health (F25) | JSON per **server** under `mobs\`, capped per mob. Recomputable — it is a cache of what the logs still say — so a corrupt or missing file just relearns (ADR-012) |
| Learned mob attacks (F26) | JSON per **server** under `attacks\`, keyed on (mob, zone, difficulty, **defender level**) — how hard something hits is a fact about a pairing, not about the mob. A rolling tally with a log-spaced hit-size histogram rather than raw samples: hits arrive three orders of magnitude faster than kills. Also a cache (ADR-013) |

## Packaging & distribution (P1)

- `dotnet publish` self-contained, with the built SPA embedded in the assembly (ASP.NET static assets). Shipped two ways (ADR-010): an Inno Setup installer (per-user by default, directory page, Start Menu entry) and a portable zip. Launch → start Kestrel on a free localhost port → open the WebView2 shell window (ADR-009).
- Update check against GitHub Releases. Installed builds download and install updates; **never without consent** — the default is to ask per release, with "not now", "skip this version", "don't ask again on this build", and an opt-in "always update automatically". Updates are staged and applied on exit, never mid-session. Portable builds only notify. Verified twice before executing: Ed25519 over the app cast and installer, then Authenticode. See ADR-010.
- Version scheme: SemVer, tag-driven releases, CI builds the publish artifact.

## Non-functional requirements

- Raid scale: 54 players, hundreds of lines/sec bursts, multi-GB files, 3+ concurrent sessions — see features.md cross-cutting section for targets.
- All dependencies permissively licensed (MIT/Apache-2.0/BSD). Keep a NOTICE file current.
- Localhost-only server; no telemetry.
- Structured logging (Microsoft.Extensions.Logging) from day one — parse anomalies (unmatched lines) logged at debug with counters, never thrown.

## Suggested build order (detail in HANDOFF.md)

1. Parser core + fixture tests (pure functions, fastest feedback).
2. Ingestion pipeline (fresh design per brief) + replay harness.
3. Session state: fights + record store + identity.
4. Query engine + canned specs, verified against hand-computed fixtures.
5. Minimal API + one live view (meter) end-to-end.
6. SPA: fight list + summaries + default dashboard.
7. Query builder UI + custom dashboards.
8. Packaging.
