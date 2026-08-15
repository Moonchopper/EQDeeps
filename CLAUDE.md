# CLAUDE.md — working on EQDeeps

Orientation for anyone (human or agent) picking this repo up. Read this first;
it exists so you don't have to re-derive the project from source every session.
It says where things are, what the rules are, and which document answers which
question. It is **not** the spec — the spec lives in `docs/`, and this file
tells you which part of it to open.

---

## 1. What this is

EQDeeps is a real-time EverQuest combat-log analytics app: a clean-room
successor to [EQLogParser](https://github.com/kauffman12/EQLogParser), built
around **composable queries** instead of fixed views. A .NET 8 localhost server
tails `eqlog_<Character>_<server>.txt`, parses it into typed records, and serves
a React SPA over REST + SignalR. The exe is a windowed WebView2 app; there is no
cloud, no telemetry, and nothing binds beyond `127.0.0.1`.

Currently **v0.11.0**, Windows-first, MIT, aiming at a public v1.

The one idea worth internalizing: **every table, chart, and live meter is a
`QuerySpec`** — a serializable description of an aggregation
(`src/EQDeeps.Core/Query/QuerySpec.cs`). The "standard views" are not bespoke
code paths; they are QuerySpecs written in code. If you find yourself adding a
special-case rendering path, check whether it should be a query first.

---

## 2. Repo map

| Path | What lives there |
|---|---|
| `src/EQDeeps.Core/` | Domain core. No ASP.NET, no Windows APIs. Parsing → events → session state → query engine. |
| `src/EQDeeps.Core/Parsing/` | Pure `string → GameEvent?` grammars. `LogEventParser` dispatches to the per-family parsers. |
| `src/EQDeeps.Core/Events/GameEvent.cs` | Every typed record in one file. Start here to learn the data model. |
| `src/EQDeeps.Core/Ingestion/` | File tailing, batching, rotation/truncation handling, timestamp seek. |
| `src/EQDeeps.Core/Session/` | `Session`, `RecordStore`, `FightTracker`, `IdentityRegistry`. |
| `src/EQDeeps.Core/Query/` | `QuerySpec`, `QueryEngine`, `MetricCatalog`, `CannedQueries`, the timelines. |
| `src/EQDeeps.Core/Mobs/` | F25 learned mob health; F26 learned mob attacks + defender levels. |
| `src/EQDeeps.Core/Maps/` | F27 zone maps: the EQ map-file grammar, the zone-name table (`zones.tsv`), the world graph. |
| `src/EQDeeps.Server/` | Minimal-API host, SignalR hub, session lifecycle, WebView2 shell, persistence stores, updates. |
| `src/EQDeeps.Server/wwwroot/` | **Build output** (gitignored). The SPA is built into here and embedded into the assembly. |
| `ui/` | React + TypeScript + Vite SPA. |
| `tests/EQDeeps.Core.Tests/` | xunit; fixture corpus in `Fixtures/*.json`. |
| `tests/EQDeeps.Server.Tests/` | xunit; end-to-end over real Kestrel + real SignalR. |
| `tests/EQDeeps.TestSupport/` | `SyntheticLogGenerator`, `SpinClock`. Shared by tests and benchmarks. |
| `tools/EQDeeps.Bench/` | Log generator + backfill/latency benchmarks. |
| `docs/` | The spec of record. See §7. |
| `installer/EQDeeps.iss` | Inno Setup script (per-user install by default). |
| `scripts/` | `publish.ps1`, `screenshots.mjs`, icon + signing setup. |
| `.github/workflows/` | `ci.yml`, `release.yml`, `verify-signing-key.yml`. |

Solution: `EQDeeps.sln`. Shared MSBuild settings in `Directory.Build.props`
(net8.0, nullable, implicit usings, **`TreatWarningsAsErrors`**) and
`Directory.Build.targets` (strips the WebView2 WPF assembly — don't remove it,
the comment there explains the MSB3277 it prevents). `global.json` pins the SDK
to the 8.0 band on purpose; read its `//` note before touching it.

---

## 3. Commands

```powershell
# Build the SPA into the backend (required before the server can serve a UI)
npm --prefix ui install        # first time only
npm --prefix ui run build

# Run the app (http://127.0.0.1:5487)
dotnet run --project src/EQDeeps.Server

# Tests — the whole solution
dotnet test

# UI dev loop: backend in one terminal, Vite in the other (Vite proxies /api + /hubs to 5487)
dotnet run --project src/EQDeeps.Server
cd ui; npm run dev

# Benchmarks / synthetic logs
dotnet run -c Release --project tools/EQDeeps.Bench -- all
dotnet run -c Release --project tools/EQDeeps.Bench -- gen $env:TEMP\eqlog_Test_server.txt 5

# Package: self-contained folder in artifacts/win-x64 (+ installer with -Installer)
powershell -File scripts/publish.ps1 -Installer
```

Useful server flags: `--browser` (default browser instead of the app window),
`--no-browser` (headless), `--stay-alive`, `--no-update-check`,
`--urls http://127.0.0.1:PORT`.

Test-only redirect flags — these keep tests out of the real `%AppData%`, and any
new store you add should get one to match: `--recentLogsRoot`, `--sampleLogRoot`,
`--updateRoot`, `--mobRoot`, `--attackRoot`, `--storeRoot`, `--mapRoot`.

**Pass all of them, always** — including for a test that only touches one. A
harness that redirects most of the stores reads as isolated, and the gap is
invisible until something writes. `--storeRoot` is the one that matters most:
it covers `DocumentStore` — dashboards, saved queries, UI settings — which is
the only store holding work the user cannot get back. It was also the last to
exist, and its absence cost a real dashboard: a UI test drove the built SPA,
which PUTs `dashboards` during its load migration before the user touches
anything, and a PUT replaces the whole document.

`--mapRoot` is the odd one out: it points at a maps folder the app only ever
*reads*, so it is about not depending on a game install rather than about
protecting anything.

### Environment traps

- **A running server locks its own binaries.** Stop the app before `dotnet build`
  or you get a file-in-use failure that looks like a compiler error.
- **Build order matters.** `npm run build` writes `src/EQDeeps.Server/wwwroot`;
  the server embeds it. Skip it and the server starts API-only with no UI — a
  deliberate fallback (`ServerApp.ResolveSpaProvider`), not a bug.
- `wwwroot/` and `artifacts/` are gitignored. Never commit build output.
- `TreatWarningsAsErrors` is on: a warning fails CI. Fix it, don't suppress it
  without a comment saying why.
- Shell is PowerShell. `&&` is not a chain operator in Windows PowerShell 5.1;
  use `;` + `if ($?)`, or the Bash tool.
- Long commit messages: write to a file and `git commit -F`, rather than
  wrestling multi-line strings through the shell.

---

## 4. How the data flows

```
eqlog_X_server.txt
  → LogFileIngestion       tail + batch, backfill phase marked separately from live
  → LogEventParser         pure: one message string → one GameEvent? (or null)
  → Session.ProcessEntry   identity signals, RecordStore.Append, FightTracker.Process
  → QueryEngine            executes a QuerySpec over the record store
  → REST (/api/...) + SignalR (/hubs/live)
  → React SPA              panels, each of which is a QuerySpec + a visualization
```

Invariants worth not breaking:

- **Parsing is a pure function of the message text.** Timestamps are attached by
  ingestion, not by the grammars. That is what makes the fixture corpus possible.
- **An unrecognized line is counted, never thrown.** `Session.UnrecognizedLines`
  and `ParserFailures` are surfaced in the API so "it's always zero" is checkable
  rather than assumed. Log lines are hostile input (see log-format doc §7):
  bounded work, length limits, no throw-on-malformed.
- **Validity toggles (bane, damage shield, headshot…) are query-time filters**,
  never ingest-time drops. Nothing is discarded during parsing.
- **No process-global parser state.** Multi-character monitoring was a day-one
  requirement; everything is session-scoped. The `IdentityRegistry` is the one
  shared thing, and it is shared *per game server*, because characters on one
  server see the same world.
- **`Session.Gate` serializes state mutation against readers.** Batch processing
  takes it; anything reading session state off another thread (query execution,
  DTO building) must too.
- **Metric formulas live in one place** (`MetricCatalog`, per
  `docs/domain/metrics-and-aggregation.md` §5) and are shared by tables, charts
  and the live meter. Denominators are the most disputed numbers in parsing —
  read §4 of that doc before inventing one.
- **Latency budget: file append → visible update ≤ 250 ms.** Backfill should
  saturate disk, not the parser. `ServerIntegrationTests` asserts the former;
  `tools/EQDeeps.Bench` measures both.

### On-disk state — `%AppData%\EQDeeps\`

| File / folder | What | Redirect flag | Recomputable? |
|---|---|---|---|
| `dashboards.json`, `saved-queries.json`, `ui-settings.json` | `DocumentStore` (key-allowlisted) | `--storeRoot` | **No — user's own work, and no history to recover from** |
| `recent-logs.json` | MRU log list | `--recentLogsRoot` | No |
| `mobs\` | F25 learned mob health per *server* | `--mobRoot` | Yes — a cache. Corrupt file just relearns |
| `attacks\` | F26 learned mob attacks per *server*, keyed by defender level too | `--attackRoot` | Yes — a cache, same deal |
| update preferences, staged installer | ADR-010 | `--updateRoot` | Yes |
| extracted demo log | bundled sample | `--sampleLogRoot` | Yes |

All stores write atomically (temp + move) and take a `root` constructor
parameter so tests can redirect them. Follow that pattern for anything new —
**and wire the flag at the same time**. `DocumentStore` took a root from the
start but was registered as a bare `AddSingleton<DocumentStore>()` for a long
while, so no flag could reach it; the parameter existing is not the same as the
redirect working, and the top row is the one where getting that wrong is
unrecoverable.

---

## 5. The frontend

Entry: `ui/src/App.tsx` (the shell: session bar, tabs, time frame, live wiring).
`ui/src/api.ts` mirrors the backend's JSON shapes — camelCase, string enums, per
`ServerApp.ConfigureJson` / `QuerySpecJson`. If you change a DTO on one side,
change it on the other in the same commit.

- `dashboards/model.ts` — the persisted dashboard/panel shape.
- `dashboards/standardViews.ts` — the read-only shipped views (healing, tanking,
  stances, experience, faction, loot), **defined in code, never stored**.
  "Customize" clones one into a real user-owned dashboard. Note
  `RETIRED_VIEW_IDS`: removed presets stay listed so the store migration keeps
  recognizing them.
- `dashboards/PanelBody.tsx` — the panel renderer (largest file in the UI).
- `dashboards/QueryBuilder.tsx` — edits a QuerySpec directly.
- `timeFrame.ts` + `timeControls.tsx` — one app-wide time frame (live tail, fight
  selection, typed window like `-6h`, absolute range, or a promoted chart zoom).
- `live.ts` — one SignalR connection for the app, per-session subscriptions via
  hub groups. Events: `backfill`, `fights`, `tick`.
- `colors.ts` / `highlight.tsx` — the shared 16-colour cycling palette and the
  linked-highlight behaviour (point at one reading of an entity, light up the
  rest everywhere).

Charting is ECharts; layout is react-grid-layout. **Every dependency must be
MIT/Apache-2.0/BSD** — check the license before adding one, and add it to
`NOTICE` if it ships.

---

## 6. Conventions

**Comments explain *why*, at the point the "why" is non-obvious.** This codebase
is heavily commented in a specific style: a doc comment states the contract, and
an inline comment records the reasoning or the incident behind a decision that
would otherwise look arbitrary (see `Directory.Build.targets`, `global.json`,
`Program.cs`'s launch behaviour, `MobHealthStore`'s class doc). Match it. Do not
add comments that restate the code.

**Commits are behavioural sentences, not changelog fragments.** Subjects read
like `Stop a missed frenzy from inventing a fight called "On a spite golem"` or
`Measure how much health a mob has from what it takes to kill one` — what
changed for the user, in plain language. Bodies are prose paragraphs explaining
the problem, the reasoning, the numbers behind it, and what was deliberately
*not* solved. Doc-only releases use `Docs: vX.Y.Z` — with the version confirmed
by the owner first, per §9.

**Branch → PR → merge.** Branches are `feat/…`, `fix/…`, `chore/…`, `docs/…`.
Work lands on `main` through a PR; CI must be green.

**Write everything as if the repo is public** — it is MIT and public release is
the goal.

**Clean-room rule.** `d:\git\EQLogParser` (Apache 2.0) is the incumbent and is
available locally. It is a **behaviour authority, not a code source**: read it to
settle a grammar or formula question, then write the answer into the domain doc
and implement it fresh. Do not port or transcribe its code. Two explicit
exceptions: its parser tests' real log lines are fine to harvest as fixture data
(game output, not creative code), and its `data/*.txt` reference files may be
copied outright with attribution in `NOTICE`.

**Documentation discipline** — this is the part that keeps future sessions cheap:

- The domain docs are the **spec of record**. When reality disagrees with them,
  fix the doc in the same change.
- Significant design choices get a short ADR in `docs/architecture/`
  (`adr-0NN-topic.md`, numbered sequentially — 015 is the newest).
- Features carry stable ids (F1…F26) in `docs/product/features.md`; update the
  status line there when one ships, and reference the id in commits and comments.
- `docs/HANDOFF.md` carries the rolling status paragraph. Keep it current.

---

## 7. Which doc answers which question

| Question | Read |
|---|---|
| What is this, who is it for, what is deliberately out of scope? | `docs/product/vision.md` |
| Is feature X shipped? What are its acceptance criteria? | `docs/product/features.md` |
| **What does this log line mean? How do I parse it?** | `docs/domain/eq-log-format.md` — the crown jewels |
| Player vs NPC vs pet vs merc; identity heuristics | same, §5 |
| **Why does one character have three levels and three classes?** | `docs/domain/eq-legends-loadouts.md` — read before touching anything level-, class- or item-related |
| What is in a map file? Why can't the log name one? | `docs/domain/eq-map-format.md` — no reference implementation exists for this one; the corpus is the authority |
| What is a fight? How is DPS/sDPS/crit rate computed? What is the denominator? | `docs/domain/metrics-and-aggregation.md` |
| Stack, component boundaries, QuerySpec model, persistence layout | `docs/architecture/system-overview.md` |
| Why is ingestion built that way? | `docs/architecture/log-ingestion-brief.md` + `adr-002` |
| Why was decision D made? | `docs/architecture/adr-001…016` (parser, ingestion, session state, query engine, API/live, SPA, dashboards, packaging, windowed shell, auto-update, gear snapshots (withdrawn), mob health, incoming damage, navigation rail, visual language, zone maps) |
| Build order, status, verification strategy | `docs/HANDOFF.md` |
| Signing, release keys, what to do before tagging | `docs/release-signing.md` |
| How do I run it / what do the flags do? | `README.md` |

---

## 8. Testing

There is **no EverQuest in the loop**. The entire product is verifiable by
writing lines to files.

- `SyntheticLogGenerator` (in `EQDeeps.TestSupport`) emits realistic scenarios —
  players, pets, heals, deaths, chat noise, timestamp pacing, the
  two-entries-on-one-line glitch, truncation and rotation. It powers unit
  fixtures, benchmarks, and end-to-end tests. Use it rather than hand-rolling
  log text.
- `tests/EQDeeps.Core.Tests/Fixtures/*.json` is the parser corpus: real log lines
  with expected parse results, per family. **Adding a grammar means adding
  fixtures.** Parsing fidelity against this corpus is a release gate.
- `ServerIntegrationTests` runs the real production pipeline (`ServerApp.Build`)
  on a dynamic port with real SignalR, appends to a temp log on a schedule, and
  asserts the push arrives inside the latency budget.
- `SpinClock` replaces wall-clock waits in ingestion tests.
- Query-engine tests check against **hand-computed** metric values, not against
  the engine's own output. Keep it that way — the point is catching a formula
  drifting, and a golden file recorded from the code under test cannot do that.

The remaining release gate that tests cannot cover: **real-log validation** —
comparing summary numbers for a real fight against EQLogParser's output for the
same file. The owner has the logs; this is still open.

---

## 9. Releasing

**Always confirm the version number with the owner before cutting a release.**
Never infer it from the diff and never tag on your own initiative — ask which
version is being cut (and whether it is a release at all) and wait for the
answer. Tags are single-use (see below), so a guessed version number is not a
mistake that can be undone: it burns that number permanently. The same applies
to every place a version is written down — `Docs: vX.Y.Z` commits, the version
in the docs, the installer, and the app cast all follow the owner's decision,
not yours.

CI (`ci.yml`) runs on every PR: restore → build SPA → build → test, on
`windows-latest`. Both workflows deliberately skip `actions/setup-dotnet`; the
runner image already ships the pinned SDK.

A release is **a pushed tag**: `git tag v0.x.y && git push origin v0.x.y` makes
`release.yml` test, publish self-contained win-x64, Authenticode-sign the exe via
Azure Artifact Signing (OIDC, no stored secret), build and sign the Inno Setup
installer, zip the portable build, generate and Ed25519-sign the NetSparkle app
cast, and create the GitHub release with everything attached at once.

Three things will bite you:

1. **Tags are single-use.** GitHub's immutable releases reserve a tag name
   permanently, even after the release is deleted — every failed attempt burns a
   version number. Run the **Verify signing key** workflow (`workflow_dispatch`)
   before tagging.
2. **Everything must be attached at creation.** Immutable releases freeze assets,
   so the app cast cannot be uploaded afterwards. That is why release notes are
   fetched from the `generate-notes` API mid-workflow.
3. **The Ed25519 public key in `Updates/UpdateService.cs` must match the private
   key in secrets.** The workflow reads the public half out of the source rather
   than duplicating it — if those drift, every client rejects every release.

Updates are consent-driven and never applied mid-session (ADR-010): staged
quietly, installed after the app closes. Nothing executes until it passes both
the Ed25519 check and Authenticode.

---

## 10. Current state and open work

Shipped: all eight build-order phases (parser, ingestion, session state, query
engine, API + live loop, SPA, dashboards, packaging), plus the WebView2 shell
(ADR-009), auto-update (ADR-010, F22),
estimated mob health (ADR-012, F25), and incoming damage (ADR-013, F26).
~1 GB/s backfill, sub-250 ms live latency.

Open, roughly in priority order:

- **Real-log validation against EQLogParser** — the v1 release gate.
- **Spell-DB integration** — class detection, bane classification, lands-on
  resolution. The reference data files are not yet copied in; `ValidityFlag.Bane`
  matches nothing until they are.
- **Identity-registry disk persistence** — it is snapshot-serializable but still
  per-server-in-memory only.
- The P1/P2 backlog in `docs/product/features.md`.
