# EQDeeps

A modern, real-time EverQuest combat-log analytics app — a clean-room successor to [EQLogParser](https://github.com/kauffman12/EQLogParser) built around composable queries and dashboards instead of fixed views.

**Status: feature-complete through packaging.** Parser core, ingestion (≈1 GB/s backfill, sub-250 ms live latency), fight/session state, the composable query engine, a localhost REST + SignalR backend, the overview dashboard (fight list, summaries, zoomable rolling-window DPS charts, live meter, ability breakdowns, deaths), custom dashboards with a full query-builder UI, preset dashboards, log autodetection, and a self-contained single-file distribution with CI releases. Remaining before public v1: real-log validation against EQLogParser (the release gate), spell-DB integration (class detection, bane, lands-on resolution), identity persistence to disk, and the P1/P2 backlog — see [features.md](docs/product/features.md).

## Run it

**From a release zip**: run `EQDeeps.Server.exe` — the app opens in its own
window (WebView2, the browser engine built into Windows 10/11). No install, no
.NET required. Closing the window exits the app (reopening backfills from the
log, so nothing is lost), and launching the exe again focuses the already-open
window. On machines without the WebView2 runtime it falls back to your default
browser, where deliberately closing the last tab shuts the app down a few
seconds later — backgrounded or slept tabs do **not** stop it. Flags:
`--browser` (use your default browser instead of the app window),
`--no-browser` (headless, no UI), `--no-update-check`, `--stay-alive` (keep
parsing with no UI open), `--urls http://127.0.0.1:PORT`.

**From source**:

```
cd ui && npm install && npm run build && cd ..   # build the SPA into the backend
dotnet run --project src/EQDeeps.Server          # http://127.0.0.1:5487
```

Point it at an `eqlog_<Character>_<server>.txt` file — installed EverQuest logs
are autodetected. No EverQuest needed to try it; generate a realistic raid log:

```
dotnet run -c Release --project tools/EQDeeps.Bench -- gen %TEMP%\eqlog_Test_server.txt 5
```

Tests: `dotnet test` · Benchmarks: `dotnet run -c Release --project tools/EQDeeps.Bench -- all` ·
UI dev loop: `dotnet run --project src/EQDeeps.Server` + `cd ui && npm run dev`.

## Package & share

`powershell -File scripts/publish.ps1` produces a self-contained
`artifacts/win-x64/` folder: one `EQDeeps.Server.exe` (SPA embedded) plus
`NOTICE.txt`, which must accompany any copy you distribute. Zip the folder and
it runs on any 64-bit Windows machine.

Pushing a git tag like `v0.1.0` makes CI test, publish, zip, and create a
GitHub release with the artifact attached (see `.github/workflows/release.yml`).

## Documentation

| Doc | Purpose |
|---|---|
| [docs/HANDOFF.md](docs/HANDOFF.md) | Implementation brief, build order, current status |
| [docs/product/vision.md](docs/product/vision.md) | What/why/who, UX pillars, non-goals |
| [docs/product/features.md](docs/product/features.md) | P0/P1/P2 feature spec with implementation status |
| [docs/domain/eq-log-format.md](docs/domain/eq-log-format.md) | EverQuest log-line taxonomy with real examples |
| [docs/domain/metrics-and-aggregation.md](docs/domain/metrics-and-aggregation.md) | Fight segmentation, counters, metric formulas |
| [docs/architecture/system-overview.md](docs/architecture/system-overview.md) | Stack, components, the QuerySpec model |
| [docs/architecture/log-ingestion-brief.md](docs/architecture/log-ingestion-brief.md) | Design brief for the file-reading layer |
| [docs/architecture/adr-001…009](docs/architecture/) | Decisions per phase: parser, ingestion, session state, query engine, API/live, SPA, dashboards, packaging, windowed shell |

Locked decisions: .NET 8 backend + React/TypeScript SPA, realtime via SignalR, multi-character monitoring from day one, permissive-license dependencies only (attribution in [NOTICE](NOTICE)), Windows-first, public release as the end goal.
