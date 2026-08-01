# EQDeeps

A modern, real-time EverQuest combat-log analytics app — a clean-room successor to [EQLogParser](https://github.com/kauffman12/EQLogParser) built around composable queries and dashboards instead of fixed views.

**Status: working app.** Parser core, ingestion (≈1 GB/s backfill, sub-250 ms live latency), fight/session state, the composable query engine, a localhost REST + SignalR backend, the React overview dashboard (fight list, summaries, DPS chart with rolling windows, live meter, ability breakdowns, deaths), and custom dashboards with a full query-builder UI (drag/resize panels, export/import as JSON). Next: packaging, spell DB (class detection, bane), real-log validation.

## Run it

**From a release**: download the zip, run `EQDeeps.Server.exe` — it starts the
local server and opens your browser. No .NET or install required. (`--no-browser`
and `--no-update-check` flags available; running it again focuses the existing
instance.)

**From source**:

```
cd ui && npm install && npm run build && cd ..   # build the SPA into the backend
dotnet run --project src/EQDeeps.Server          # http://127.0.0.1:5487
```

**Package it yourself**: `powershell -File scripts/publish.ps1` → a single
self-contained `artifacts/win-x64/EQDeeps.Server.exe` with the SPA embedded.
Tagged pushes (`v*`) build and attach the zip to a GitHub release via CI.

Open the app in a browser and point it at an `eqlog_<Character>_<server>.txt`
file. No EverQuest needed to try it — generate a realistic raid log with:

```
dotnet run -c Release --project tools/EQDeeps.Bench -- gen %TEMP%\eqlog_Test_server.txt 5
```

Tests: `dotnet test` · Benchmarks: `dotnet run -c Release --project tools/EQDeeps.Bench -- all`
UI dev loop: `dotnet run --project src/EQDeeps.Server` + `cd ui && npm run dev`.

| Doc | Purpose |
|---|---|
| [docs/HANDOFF.md](docs/HANDOFF.md) | **Start here** — implementation brief, build order, verification strategy |
| [docs/product/vision.md](docs/product/vision.md) | What/why/who, UX pillars, non-goals |
| [docs/product/features.md](docs/product/features.md) | P0/P1/P2 feature spec with acceptance criteria |
| [docs/domain/eq-log-format.md](docs/domain/eq-log-format.md) | EverQuest log-line taxonomy with real examples |
| [docs/domain/metrics-and-aggregation.md](docs/domain/metrics-and-aggregation.md) | Fight segmentation, counters, metric formulas |
| [docs/architecture/system-overview.md](docs/architecture/system-overview.md) | Stack, components, the QuerySpec model |
| [docs/architecture/log-ingestion-brief.md](docs/architecture/log-ingestion-brief.md) | Design brief for the file-reading layer |

Locked decisions: .NET 8 backend + React/TypeScript SPA, realtime via SignalR/WebSockets, multi-character monitoring from day one, permissive-license dependencies only, Windows-first, public release as the end goal.
