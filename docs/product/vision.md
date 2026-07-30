# EQDeeps — Product Vision

## What this is

EQDeeps is a modern combat-log analytics app for EverQuest (Live/TLP servers, with best-effort EMU support). EverQuest writes a plain-text log file per character (`/log on` in game); every combat hit, heal, spell cast, death, loot drop, and chat message the character witnesses becomes a timestamped line. EQDeeps reads those files — both historically and live while the game runs — and turns them into fight-by-fight performance analytics: DPS parses, healing breakdowns, tanking summaries, death recaps, and anything else the user cares to compose.

It is a clean-room successor to [EQLogParser](https://github.com/kauffman12/EQLogParser) (the de-facto standard tool). We keep its concept and hard-won domain knowledge — documented in `docs/domain/` — but rewrite from scratch with a different UX philosophy and a modern architecture.

## Who it's for

- **Raiders** who watch live DPS/healing meters during a raid night and review fights between pulls.
- **Raid/guild leaders** who compare players, spot deaths and their causes, and paste parse summaries into chat.
- **Group players and soloers** tuning their character's performance.
- Windows users running EverQuest; the app runs on the same machine as the game.

## Why a new app

Existing tools (EQLogParser, GamParse) are capable but constrained:

1. **Fixed views.** Every table and chart is a hard-coded screen. If you want "my rolling DPS next to the raid's healing received, only for the last 3 pulls," you can't have it.
2. **Dated desktop UX.** WPF-era docking panels, dense grids, modal config.
3. **Licensing friction.** EQLogParser depends on commercial UI components (Syncfusion), complicating community contribution and redistribution.
4. **Stale-data workflows.** Toggles like "include bane damage" are applied at ingest time, forcing full recomputes; real-world latency from log line to screen is 1–2 s.

## UX pillars (in priority order)

### 1. Composable aggregation and visualization, with sensible defaults

The core mental model: **every table and chart is a view over a query**, and the user can edit the query. A query is roughly *metric × dimensions × filters × time window × bucketing* (see `docs/architecture/system-overview.md`). Out of the box, canned queries replicate the classic parses — Damage Summary, Healing Summary, Tanking Summary, DPS-over-time — so a new user never has to build anything. But every canned view has an "edit" affordance that opens the same builder a power user starts from.

Example user stories:

- "Show damage by *player*, but let me re-group it by *spell* or by *target* with one click."
- "Filter this DPS chart to just the melee classes, last 2 fights only."
- "Exclude damage-shield damage from this view" — as a query-time filter, instantly, without reparsing.
- "Bucket healing into 6-second windows instead of per-second."

### 2. Real-time

While the game runs, the app follows the log live. Fight list, meters, and any dashboard panel marked "live" update within a fraction of a second of the line hitting the file. No refresh button, no reopening files.

### 3. Dashboards

Users compose pages of panels — each panel is a query + a visualization (table, line chart, bar chart, heatmap, big-number stat tile) — arranged on a grid: drag, resize, save, duplicate. A default dashboard ships pre-built (fight list + damage summary + DPS-over-time + deaths). Layouts persist across sessions and can be exported/shared as JSON.

## Product principles

- **Sensible defaults first.** Every escape hatch for power users must not tax the user who just wants a DPS parse. Opening the app to a monitored log should produce useful output with zero configuration.
- **Never make the user reparse.** Anything that can be a query-time decision (filters, groupings, toggles) must be one. Reparsing is reserved for opening new files.
- **Multi-character is table stakes.** Boxers and raid leaders monitor several logs at once; character is just another dimension in the query model.
- **Permissively licensed everywhere.** The app will be released publicly. No commercial or copyleft-encumbered dependencies.
- **The old app is a reference, not a template.** When in doubt about *what a log line means* or *what a metric formula is*, EQLogParser's behavior is the authority (see `docs/domain/`). How we build it is entirely open.

## Non-goals (v1)

- **No in-game overlay windows.** No always-on-top meters over the EQ client.
- **No triggers/audio system.** No GINA-style pattern alerts, TTS, or timers (a future version may revisit; the architecture shouldn't preclude it).
- **No cross-machine or hosted service.** Local, single-user app. (Clean separation of backend/frontend keeps the door open.)
- **Not a chat client, damage simulator, or gear planner.**

## Success criteria for the first working pass

1. Open one or more character logs; historical fights appear; a raid-night log (multi-GB) backfills fast enough to feel instant for the recent past.
2. With the game writing to the log, a fight in progress shows a live-updating damage summary and DPS chart.
3. The user can change what a panel shows (metric/grouping/filter) without any reparse.
4. The default dashboard is genuinely useful to a raider with zero configuration.
