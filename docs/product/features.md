# EQDeeps — Feature Specification

Priorities: **P0** = first working pass, **P1** = v1 public release, **P2** = later. Each feature has acceptance criteria (AC). The feature inventory is informed by the reference implementation (EQLogParser); v1 deliberately drops its overlay/trigger/audio subsystems.

**Implementation status (2026-08-02):** ✅ shipped — F1, F2, F3, F4, F5, F6, F7,
F8, F14. Beyond spec: log autodetection (running process/registry/known paths),
aggregate selection stats, by-target grouping, rolling-window + zoomable DPS
charts, ability breakdown chart with per-attacker stacks, app-wide pet-rollup
toggle, cross-panel entity colors with tinted table rows, one app-wide time frame (F7a), standard views (F7b),
Gantt-style event timeline (per-PC/NPC lanes: casts, activated abilities,
deaths, resists, plus buff spans paired from the owner's cast → named
"worn off" messages; `POST /api/sessions/{id}/timeline` is the seed of the
event system that will annotate DPS/heal charts — spell-DB integration adds
received buffs and true durations later).
⏳ pending — F9 (death recap), F10 (spell/cast analytics — needs the spell DB),
F11, F12, F13 (identity persists in-memory per server with serializable
snapshots; the disk read/write wiring remains), F15–F21. Release gate still
open: real-log validation against EQLogParser. Design decisions live in
`docs/architecture/adr-00*.md`.

---

## P0 — First working pass

### F1. Log file management (multi-character)

Open, monitor, and close multiple EverQuest log files concurrently. Files follow the `eqlog_<Character>_<server>.txt` naming convention; character and server are parsed from the filename. Support plain `.txt` and gzipped archives (`.gz`) for historical viewing. Remember recently opened files and re-open monitored files on startup.

- AC: Two logs monitored simultaneously produce independent, correctly attributed fight data; character/server shown per session.
- AC: A remembered log can be removed from the recent list (test files, one-off copies) without deleting the file; the removal survives a restart. Installed-log discovery is unaffected — only logs listed with source `recent` can be removed, since anything found by scanning an EverQuest install would reappear on the next scan.
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

### F7a. One time frame

Time is the primary axis, not fights. Every record has a timestamp, and much of what matters — XP, faction, loot, downtime itself — happens outside any fight, so a fight is a derived artifact (the parser's read of where a pull started and stopped) rather than the thing everything hangs off. The app therefore has exactly one time frame, and every panel reports over it.

The frame is either a **live tail** — the trailing span of the record stream, anchored to the newest record, which is what "following live" amounts to — or a **fixed range**, produced by the fight list. There is no separate follow-live flag: a live frame is already following.

The fight list is a **range selector**, not a filter. Click frames one fight, shift-click extends to frame everything between in list order, ctrl/cmd-click adds or removes one, a group header frames the pull chain. What is picked becomes the window between the first and last fight chosen, downtime included. Because it is a window, combat from other fights inside it counts too — concurrent mobs, or a long pull straddling the edge.

Combat still aggregates per fight *within* the frame, so DPS over a framed stretch means what it meant when those fights were selected directly, rather than damage averaged across the downtime between them. Progression sources take the frame whole, which is what makes a range worth having.

A single **reset** in the top bar returns the frame to live and the window/span to their defaults; "back to live" in the fight list releases a range without touching the settings.

Every time chart draws **fight bands** behind the line: faint alternating shading over the stretches where something was being fought, labelled with the mob. Without them a trough reads the same whether you were between pulls, running to the next camp, or fighting something that did not hurt. Names are anchored at the floor and read upward, clamped to the plot height so a long mob name truncates rather than running off the top. The bands are capped — names drop out past ~18 fights in view, and the shading stops entirely past ~120, because solid shading is not context.

- AC: Selecting fights changes what every panel shows, including Experience, Faction and Loot.
- AC: A frame covering isolated sequential fights reports the same total, active seconds and DPS as selecting those fights directly.
- AC: A live frame updates as records arrive, with nothing to re-select.
- AC: A chart zoomed to a few pulls names the mobs behind the line; one zoomed out to a whole evening shades without naming, and one showing days shades not at all.

### F7b. Standard views

Overview is a section, not a page: a row of sub-tabs holds Summary (the F7 dashboard) plus the specialized standard views — Healing, Tanking, Experience, Faction, Loot. Damage rankings and a live "right now" view are deliberately absent: Summary already carries both, and a standard view has to earn its tab. These ship with the app rather than being provisioned into the user's dashboard store, so they are read-only and cannot drift, be deleted, or be confused with something the user built. "Customize a copy" clones one into a custom dashboard (F8) that the user then owns.

Window and span are presentation, not properties of a panel, so no panel definition carries them: there is exactly one default (`DEFAULT_CHART_SETTINGS`) and every chart in the app starts there. The top bar owns it — a control beside the version number — and changing it pushes down to every chart, Summary's DPS chart included. The setting persists across restarts.

Individual charts can still deviate: each time panel repeats the same controls in its header, with "apply to all" to put the rest of that view on its footing. A deviation lasts until the parent setting changes, which clears it rather than leaving some charts silently behind. Both ladders are multiples of the panel's bucket width, so a minute-bucketed chart offers minute-scale windows rather than the 1-second chart's seconds.

The span is the query, not just the picture: a whole-log panel is scoped to the span being viewed, so a total or a table beside a chart counts exactly the seconds the chart draws. Span "fit" means the whole log. Time charts fetch one extra rolling window of history beyond the span so the mean is warm at the left edge; that history sits outside the drawn axis.

Panels keep `bucketSeconds`, which is a different thing: it is a query parameter deciding what the server aggregates, not how the result is read.

- AC: The standard views cannot be edited, deleted or exported in place; "customize a copy" produces an editable dashboard and leaves the standard view unchanged.
- AC: A fresh profile opens every time chart — Summary's and every standard view's — on the same window and span.
- AC: Changing the top-bar setting moves every chart in the app, discarding per-panel deviations.
- AC: Every panel in a view reports over the same time frame — narrowing the span lowers the totals and tables, it does not merely crop the charts.
- AC: Changing window or span on one chart and pressing "apply to all" moves every other time chart in that view to the same setting.
- AC: Time charts draw one continuous line — a bucket with no events reads as zero, not a hole — so idle stretches sag to the axis instead of fragmenting the chart. Ranges too large to fill point-by-point fall back to breaking on long gaps rather than degrading.
- AC: The chosen sub-tab survives a restart.

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

Self-contained Windows distribution (single exe or installer) that starts the local backend and shows the UI in the app's own window (WebView2 shell; default-browser fallback). Update check against GitHub Releases with one-click download.

- AC: A machine without the .NET SDK runs the app from the published artifact.

### F22. Consent-driven auto-update

Installed builds keep themselves current without ever surprising the user. An Inno Setup installer (per-user by default, real directory page) plus a NetSparkle update loop; see [ADR-010](../architecture/adr-010-auto-update.md).

The consent model is the feature, not the downloading. The default is to ask once per release, and every way of saying "no" states how long it lasts: *not right now* (until restart), *skip this version* (until something newer ships), *don't ask again for vX.Y.Z* (until the user is on a different build), or *always update automatically*. Preferences persist server-side, so they hold with no UI attached.

- AC: An update is never applied mid-session — downloads stage quietly and install on exit.
- AC: Declining is always reversible; an explicit "check now" overrides every standing decline.
- AC: Nothing downloaded is executed unless it passes both Ed25519 and Authenticode verification.
- AC: Portable and unkeyed builds degrade to notify-only rather than pretending to install.

---

## P2 — Later

- **F15. Chat archive & search** — persist chat by channel/player with full-text search and date ranges.
- **F16. Loot & random-roll tracking** — looted items, currency splits, /random winners.
- **F17. Log archiving** — scheduled or zone-triggered rotation/compression of giant EQ logs (the game appends forever; multi-GB files are normal).
- **F18. ADPS awareness** — track crit-modifying buffs to contextualize damage spikes (reference: adpsMeter data in old app).
- **F19. Report export** — HTML/CSV export of any view; shareable fight report bundles.
- **F20. Trigger system** — GINA-style pattern alerts (explicit non-goal for v1; keep the ingestion layer's line stream subscribable so this can attach later).
- **F21. Mob-normalized DPS context** — cross-fight DPS aggregates are skewed by level differences and mob mitigation. Ship/derive an NPC-stats database (level, class, AC/mitigation tier per zone/era) and use it to annotate aggregate DPS with expected upper/lower bounds per target, so "average DPS" comparisons across different content are honest. (Owner request, 2026-08-01; aggregate selection UI ships first and accepts the skew.)

---

## Cross-cutting requirements

- **Latency:** file-append → visible update ≤ 250 ms target (old app: 1–2 s).
- **Backfill throughput:** historical load should saturate disk read, not parser — target ≥ 100 MB/s on typical hardware; a 1 GB log's last raid night loads in seconds. (Old app parses a full file in minutes on large logs.)
- **Scale:** 54-player raids, hundreds of combat lines/second burst, logs up to several GB, fights lasting 10+ minutes, sessions monitoring 3+ characters.
- **Correctness:** parsing fidelity against the fixture corpus (see HANDOFF.md verification section) is a release gate.
- **Licensing:** all dependencies MIT/Apache-2.0/BSD-compatible. If any data files or fixtures are copied from EQLogParser (Apache 2.0), preserve attribution in a NOTICE file.
