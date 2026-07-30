# EQDeeps — Feature Specification

Priorities: **P0** = first working pass, **P1** = v1 public release, **P2** = later. Each feature has acceptance criteria (AC). The feature inventory is informed by the reference implementation (EQLogParser); v1 deliberately drops its overlay/trigger/audio subsystems.

---

## P0 — First working pass

### F1. Log file management (multi-character)

Open, monitor, and close multiple EverQuest log files concurrently. Files follow the `eqlog_<Character>_<server>.txt` naming convention; character and server are parsed from the filename. Support plain `.txt` and gzipped archives (`.gz`) for historical viewing. Remember recently opened files and re-open monitored files on startup.

- AC: Two logs monitored simultaneously produce independent, correctly attributed fight data; character/server shown per session.
- AC: Reopening the app restores the previously monitored files without user action.
- AC: A log being actively written by EverQuest can be opened without error (EQ holds a write handle; the file must be opened with shared read/write/delete access).
- AC: Historical backfill offers "load last N hours/days" as well as "entire file."

### F2. Live tailing with rotation handling

While monitoring, new lines are ingested continuously. Handle: file truncation (user deletes/clears the log in-game), file rename/rotation (archiving tools move the file and EQ recreates it), and the app being started mid-raid (seek to a requested backfill point, then go live).

- AC: Appending lines to a monitored file (e.g., with a script — no EverQuest needed) updates visible stats within 250 ms.
- AC: Truncating or swapping the file does not crash or duplicate data; tailing resumes on the new content.

### F3. Fight detection and fight list

Segment the record stream into fights per the rules in `docs/domain/metrics-and-aggregation.md` (keyed by NPC, inactivity timeouts). Present a chronological fight list showing name, start time, duration, total damage/tank damage, with visual grouping of pulls separated by inactivity gaps ("break time"). Users select one or more fights (or a group) as the scope for analysis views.

- AC: Replaying a canned raid-log fixture produces the expected fight list (names, boundaries, totals) matching the reference implementation's segmentation rules.
- AC: Selecting multiple fights unions their time ranges; all analysis panels rescope.
- AC: During live play, the in-progress fight appears immediately and its totals tick.

### F4. Classic parses as canned queries

Damage Summary, Healing Summary, and Tanking Summary tables for the selected fights — per-player rows with the classic columns (totals, DPS/SDPS, percent-of-total, crit/lucky/twincast rates, etc. per the metric catalog), pets rolled into owners with expandable breakdown, drill-down from player → spell/skill → per-hit detail.

- AC: For a canned fixture log, summary numbers (total, DPS, crit rate) match hand-computed values from the formula catalog.
- AC: Pet damage appears under "Owner +Pets" and can be expanded.
- AC: Each summary is implemented as a canned query spec, not a bespoke code path — proven by the user being able to open it in the query editor (F6).

### F5. Real-time meter

A live view (dashboard panel) of the current/recent fights: per-player damage or healing with DPS, updating continuously, resettable, scoped to "current fight" or "all fights since reset."

- AC: Meter reflects new log lines within 250 ms; ordering and totals stable under raid-scale line rates (hundreds of lines/sec).

### F6. Composable query model

The heart of the product (see `docs/architecture/system-overview.md` for the spec sketch). Users can create or edit a view by choosing: metric (damage, healing, tanking, DPS, crit rate, hit count…), grouping dimensions (player, spell, target, character, class, damage type…), filters (players, classes, spell names, damage-validity toggles like bane/damage-shield, min/max time window within the selection), and bucketing (per-second, N-second, whole-fight).

- AC: The damage-validity toggles (bane, damage shield, headshot, assassinate, finishing blow, slay undead) are query-time filters — flipping one updates the view without reparsing.
- AC: "Damage by spell for player X" and "healing received by player X" are constructible from the UI in under a minute by a novice.
- AC: A query can be saved with a name and reused across panels/dashboards.

### F7. Default dashboard

One built-in dashboard: fight list + damage summary + DPS-over-time chart + death log for the selection. Panels live-update.

- AC: Fresh install + open log → this dashboard renders with data and no configuration.

---

## P1 — Public v1

### F8. Custom dashboards

Create/duplicate/delete dashboards; add panels (table, line chart, bar chart, stat tile, heatmap); drag/resize on a grid; layouts persisted; export/import a dashboard as JSON.

- AC: A layout survives app restart; an exported JSON imports on another machine.

### F9. Death recap

For each player death: the final ~20 seconds of incoming damage, heals received, and buffs/debuffs landing, interleaved chronologically with running HP-relevant totals; accessible from summaries and the fight list.

- AC: Recap for a fixture death matches the interleaving rules in the domain doc.

### F10. Spell/cast analytics

Cast counts per player (casts, interrupts, twincasts), received-buff counts, spell damage breakdowns (DD vs DoT vs proc), resist tracking per spell and per NPC.

### F11. Hit distribution views

Histogram of hit sizes (crit vs non-crit) per player/skill; timeline density views.

### F12. Parse-to-clipboard

One-click formatted text summary of the selected fights (top-N players by damage/healing/tanking, in the classic "paste into guild chat" format).

- AC: Output text matches the reference format conventions (K/M/B abbreviations, rank ordering).

### F13. Player identity & pet mapping persistence

Learned verified-players, pet→owner mappings, and class detections persist per server and improve over time; users can correct/override (assign pet to player, set a player's class, merge a renamed character).

### F14. Packaging & updates

Self-contained Windows distribution (single exe or installer) that starts the local backend, serves the UI, and opens the browser (and/or tray icon). Update check against GitHub Releases with one-click download.

- AC: A machine without the .NET SDK runs the app from the published artifact.

---

## P2 — Later

- **F15. Chat archive & search** — persist chat by channel/player with full-text search and date ranges.
- **F16. Loot & random-roll tracking** — looted items, currency splits, /random winners.
- **F17. Log archiving** — scheduled or zone-triggered rotation/compression of giant EQ logs (the game appends forever; multi-GB files are normal).
- **F18. ADPS awareness** — track crit-modifying buffs to contextualize damage spikes (reference: adpsMeter data in old app).
- **F19. Report export** — HTML/CSV export of any view; shareable fight report bundles.
- **F20. Trigger system** — GINA-style pattern alerts (explicit non-goal for v1; keep the ingestion layer's line stream subscribable so this can attach later).

---

## Cross-cutting requirements

- **Latency:** file-append → visible update ≤ 250 ms target (old app: 1–2 s).
- **Backfill throughput:** historical load should saturate disk read, not parser — target ≥ 100 MB/s on typical hardware; a 1 GB log's last raid night loads in seconds. (Old app parses a full file in minutes on large logs.)
- **Scale:** 54-player raids, hundreds of combat lines/second burst, logs up to several GB, fights lasting 10+ minutes, sessions monitoring 3+ characters.
- **Correctness:** parsing fidelity against the fixture corpus (see HANDOFF.md verification section) is a release gate.
- **Licensing:** all dependencies MIT/Apache-2.0/BSD-compatible. If any data files or fixtures are copied from EQLogParser (Apache 2.0), preserve attribution in a NOTICE file.
