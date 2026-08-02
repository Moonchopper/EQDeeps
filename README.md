# EQDeeps

A modern, real-time EverQuest combat-log analytics app — a clean-room successor to [EQLogParser](https://github.com/kauffman12/EQLogParser) built around composable queries and dashboards instead of fixed views.

![EQDeeps overview dashboard on a synthetic raid log](docs/media/overview.png)

**Status: v0.1.0 released.** Parser core, ingestion (≈1 GB/s backfill, sub-250 ms live latency), fight/session state, the composable query engine, a localhost REST + SignalR backend, the overview dashboard (fight list, summaries, zoomable rolling-window DPS charts, live meter, ability breakdowns, deaths), custom dashboards with a full query-builder UI, preset dashboards, log autodetection, a WebView2 windowed shell, and a self-contained single-file distribution with CI releases — validated through day-to-day use on real logs. Remaining before public v1: a systematic number-comparison harness against EQLogParser, spell-DB integration (class detection, bane, lands-on resolution), identity persistence to disk, and the P1/P2 backlog — see [features.md](docs/product/features.md).

## Run it

**Installer** (recommended, [latest release](https://github.com/Moonchopper/EQDeeps/releases/latest)):
run `EQDeeps-Setup-x.y.z.exe`. It installs for you only, under
`%LocalAppData%\Programs\EQDeeps`, with no administrator rights needed — the
wizard still lets you pick any folder, or install for all users if you'd rather.
Installed copies **keep themselves up to date**: see [Updates](#updates) below.

**Portable zip** (same release): unzip and run `EQDeeps.Server.exe` — nothing is
written outside the folder and deleting it removes the app. Portable copies
tell you when a new release exists but can't install it for you.

Either way the app opens in its own
window (WebView2, the browser engine built into Windows 10/11). No
.NET required. Closing the window exits the app (reopening backfills from the
log, so nothing is lost), and launching the exe again focuses the already-open
window. On machines without the WebView2 runtime it falls back to your default
browser, where deliberately closing the last tab shuts the app down a few
seconds later — backgrounded or slept tabs do **not** stop it. Flags:
`--browser` (use your default browser instead of the app window),
`--no-browser` (headless, no UI), `--no-update-check`, `--stay-alive` (keep
parsing with no UI open), `--urls http://127.0.0.1:PORT`.

> **First run:** releases are signed, but Windows SmartScreen builds reputation
> per file hash, so a brand-new release can still show "Windows protected your
> PC" until enough people have run it. Click **More info → Run anyway**; the
> prompt stops appearing as a release circulates.

## Updates

Installed copies check GitHub for new releases and, by default, **ask you once
per release** before doing anything. The prompt offers:

| | What it does |
| --- | --- |
| **Update** | Downloads in the background; installs the next time you close EQDeeps |
| **Update & restart now** | Installs straight away and reopens on the new version |
| **Not right now** | Asks again next launch |
| **Skip this version** | Silent until something newer than that release ships |
| **Don't ask again for vX.Y.Z** | Silent until you're running a different version |
| **Update automatically from now on** | Stops asking; every release installs itself |

By default updates are **never applied mid-session** — a download is staged
quietly and the swap happens after you close the app, so a raid parse is never
interrupted. The pill beside the version number shows what's happening and
offers **restart to update** if you want it sooner. Nothing is executed until it
passes both an Ed25519 signature check against a key built into the app and a
Windows Authenticode check.

Beside the version number, **⚙** opens update preferences (ask each time /
automatic / never check) and **⟳** checks on demand. A manual check overrides
every standing "no", including automatic mode — it always asks before installing,
so it's the way back if you've chosen "don't ask again". Long-running sessions
re-check every couple of hours on their own, so you don't have to restart to
find out a release exists.

Prefer no network calls at all? `--no-update-check` disables the whole thing for
a run, and the in-app setting has a "never check" mode.

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
`artifacts/win-x64/` folder: `EQDeeps.Server.exe` (SPA embedded) with its
runtime, plus `NOTICE.txt`, which must accompany any copy you distribute. Zip
the folder and it runs on any 64-bit Windows machine. Add `-Installer` to also
compile `artifacts/installer/EQDeeps-Setup-x.y.z.exe` — that needs
[Inno Setup 6](https://jrsoftware.org/isdl.php)
(`winget install JRSoftware.InnoSetup`).

Releases ship by pushing a git tag: `git tag v0.x.y && git push origin v0.x.y`
makes CI test, publish, sign, build the installer, and create a GitHub release
with the installer, the portable zip, and the signed app cast that drives
auto-update (see `.github/workflows/release.yml`) — that's how
[v0.1.0](https://github.com/Moonchopper/EQDeeps/releases/tag/v0.1.0) was built.

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
| [docs/architecture/adr-001…010](docs/architecture/) | Decisions per phase: parser, ingestion, session state, query engine, API/live, SPA, dashboards, packaging, windowed shell, auto-update |
| [docs/release-signing.md](docs/release-signing.md) | Azure Artifact Signing setup for signed releases and auto-update |

Locked decisions: .NET 8 backend + React/TypeScript SPA, realtime via SignalR, multi-character monitoring from day one, permissive-license dependencies only (attribution in [NOTICE](NOTICE)), Windows-first, public release as the end goal.

## License

[MIT](LICENSE). Third-party attributions live in [NOTICE](NOTICE), which must
accompany any distributed copy. EverQuest is a registered trademark of Daybreak
Game Company LLC; EQDeeps is an unaffiliated fan-made tool.
