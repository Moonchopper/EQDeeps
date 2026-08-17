# EQDeeps — Feature Specification

Priorities: **P0** = first working pass, **P1** = v1 public release, **P2** = later. Each feature has acceptance criteria (AC). The feature inventory is informed by the reference implementation (EQLogParser); v1 deliberately drops its overlay/trigger/audio subsystems.

**Implementation status (2026-08-02):** ✅ shipped — F1, F2, F3, F4, F5, F6, F7,
F8, F14. Beyond spec: log autodetection (running process/registry/known paths),
aggregate selection stats, by-target grouping, rolling-window + zoomable DPS
charts, ability breakdown chart with per-attacker stacks, app-wide pet-rollup
toggle, cross-panel entity colors with tinted table rows, one app-wide time frame (F7a), standard views (F7b),
Gantt-style event timeline (per-PC/NPC lanes with alternating banding and a time grid; cast marks are sized by what the cast landed, paired from the following damage/heal records, damage and healing on separate frame-wide scales and split by hue so a large mark is never ambiguous: casts, activated abilities,
deaths, resists, plus buff spans paired from the owner's cast → named
"worn off" messages; `POST /api/sessions/{id}/timeline` is the seed of the
event system that will annotate DPS/heal charts — spell-DB integration adds
received buffs and true durations later).
⏳ pending — F9 (death recap), F10 (spell/cast analytics — needs the spell DB),
F11, F12, F13 (identity persists in-memory per server with serializable
snapshots; the disk read/write wiring remains), F15–F21. Release gate: fixture
fidelity (enforced) plus the consistency invariants described in CLAUDE.md §8
(defined, not yet written) — validating against EQLogParser was retired on
2026-08-16, since it parses live EverQuest and this app is used on Legends.
Design decisions live in `docs/architecture/adr-0*.md`.

**Update (2026-08-06):** F25 (estimated mob health) shipped — instance
difficulty is parsed off the zone line, kills are measured into a per-server
index, and the Mobs tab, the fight-list share column and the tier ladder read
it.

**Update (2026-08-08):** F26 (incoming damage) shipped — an Incoming sub-tab
carrying the raw feed of swings taken and a per-server attack index keyed on the
defender's level as well as the mob, zone and difficulty.

**Update (2026-08-16):** F28 (log cache) shipped — a log is parsed once; its
records are cached on disk and the next open restores them and resumes at the
byte where the last one stopped, ~3× faster, and the session holds half the
memory it did (issue #59, ADR-018).

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

A result table is a list you interrogate, not a ranking you read top-down, so every table panel carries a fuzzy search box and sortable columns. Both work over the rows already fetched — a panel returns its whole grouped tree in one response — so filtering is a re-render rather than a round trip, and the list keeps up with typing. Matching a row and matching only its children mean different things: match a row and its full breakdown stays, match only a descendant and the row narrows to the matching children and opens itself, because the answer is the thing a level down. Sorting cycles to "off" as well as both directions, since "off" is the server's own ranking and losing the way back would make sorting a one-way door.

Rows carry a meter fill sized against the biggest value at their level. Top-level rows fill in their entity color — identity, never rank, per the cross-panel color rule. Breakdown rows fill in a heat ramp instead, because under an expanded row the entity is already named by the parent and "which of these is the big one" is the whole question. The ramp runs the palette's danger → gold → live rather than red straight to green, so lightness moves with hue and the ordering survives red-green color blindness.

- AC: The damage-validity toggles (bane, damage shield, headshot, assassinate, finishing blow, slay undead) are query-time filters — flipping one updates the view without reparsing.
- AC: "Damage by spell for player X" and "healing received by player X" are constructible from the UI in under a minute by a novice.
- AC: A query can be saved with a name and reused across panels/dashboards.
- AC: Typing in a table's search box narrows it without a server round trip; a query matching only a child row surfaces its parent already expanded.
- AC: Clicking a column header sorts every level of the tree, and a third click restores the server's ranking.

### F7. Default dashboard

One built-in dashboard: fight list + damage summary + DPS-over-time chart + death log for the frame. Panels live-update.

Charts own a wide scrolling column; tables live in a narrow rail beside it. The two scroll independently, so a raid-sized damage table never squeezes the charts and a stack of charts never pushes the tables off screen — the previous equal-height rows gave a one-row table as much of the page as every chart combined. Each chart claims a comfortable minimum height and grows into spare room rather than shrinking below it, because a trend read at 120px is not a trend read. The wide column carries the DPS chart, healing and damage-taken trends side by side, and the timeline; the rail carries the damage table, the ability breakdown, the live meter and deaths. The trends are rendered from the same panel definitions the standard views use, so output, upkeep and incoming damage sit on one screen and one time frame. The fight list collapses to a spine — it stays visible rather than vanishing, because the frame it set is still in force and there has to be a way back.

- AC: Fresh install + open log → this dashboard renders with data and no configuration.

### F7a. One time frame

Time is the primary axis, not fights. Every record has a timestamp, and much of what matters — XP, faction, loot, downtime itself — happens outside any fight, so a fight is a derived artifact (the parser's read of where a pull started and stopped) rather than the thing everything hangs off. The app therefore has exactly one time frame, and every panel reports over it.

The frame is either a **live tail** — the trailing span of the record stream, anchored to the newest record, which is what "following live" amounts to — or a **fixed range**, produced by the fight list. There is no separate follow-live flag: a live frame is already following.

The range need not come from the fight list. Any zoomed chart offers **set as time range**, which promotes the window it is showing to the app-wide frame — the way to frame a wipe, a lull, or the two minutes either side of a death, none of which is a pull. The top-bar control is a picker rather than a dropdown: quick spans from 30 seconds to a day, a typed relative window (`-6h`, `20m`, `500m`, `1h30m` — units required, since a bare `500` could be seconds or minutes and guessing wrong is 60x off), and an absolute from/to for a window that has nothing to do with now.

The fight list is a **range selector**, not a filter. Click frames one fight, shift-click extends to frame everything between in list order, ctrl/cmd-click adds or removes one, a group header frames the pull chain. What is picked becomes the window between the first and last fight chosen, downtime included. Because it is a window, combat from other fights inside it counts too — concurrent mobs, or a long pull straddling the edge.

Combat still aggregates per fight *within* the frame, so DPS over a framed stretch means what it meant when those fights were selected directly, rather than damage averaged across the downtime between them. Progression sources take the frame whole, which is what makes a range worth having.

A live frame can also follow the **wall clock** rather than the newest record (a "scroll" toggle in the top bar, on by default). The server anchors a trailing window to the newest record, so with the log quiet the picture freezes — which reads as a broken chart rather than as nothing happening. Scrolling keeps the window advancing and draws the quiet time as the zero it is, with the rolling mean decaying into it. It stops when the log has gone an hour without a line, since chasing the clock across an archived log would show a window of pure zeros with the data hours to the left, and it yields to an active zoom.

A single **reset** in the top bar returns the frame to live and the window/span to their defaults; "back to live" in the fight list releases a range without touching the settings.

Query cost is scaled to the range rather than fixed: the bucket widens so a long view fetches roughly the points it can draw, the rolling window widens with it so the smoothing keeps its shape, and every panel's refresh backs off to about one bucket (capped) since nothing can change faster than a bucket closes. The timeline additionally caps the lanes it draws and says how many it left out.

Every time chart draws **fight bands** behind the line: faint alternating shading over the stretches where something was being fought, labelled with the mob. Without them a trough reads the same whether you were between pulls, running to the next camp, or fighting something that did not hurt. Names are anchored at the floor and read upward, clamped to the plot height so a long mob name truncates rather than running off the top. One app-wide setting in the top bar governs the whole overlay: off, bands (shading with no names), or names at small / medium / large, defaulting to large. Whether a band is named is decided per band by measurement: rotated text is only as wide as its font size, so a band earns a name when it spans more pixels than that. Dense views therefore thin out rather than switching off, and the shading itself stops entirely past ~120 bands, because solid shading is not context.

- AC: Selecting fights changes what every panel shows, including Experience, Faction and Loot.
- AC: A frame covering isolated sequential fights reports the same total, active seconds and DPS as selecting those fights directly.
- AC: A live frame updates as records arrive, with nothing to re-select.
- AC: A chart zoomed to a few pulls names the mobs behind the line; one zoomed out to a whole evening shades without naming, and one showing days shades not at all.

### F7b. Standard views

Overview is a section, not a page: a **left nav rail** holds Summary (the F7 dashboard) plus the specialized standard views — Healing, Tanking, Stances (F23, only on logs that have them), Experience, Faction, Loot, Mobs, Incoming. Damage rankings and a live "right now" view are deliberately absent: Summary already carries both, and a standard view has to earn its place. These ship with the app rather than being provisioned into the user's dashboard store, so they are read-only and cannot drift, be deleted, or be confused with something the user built. "Customize a copy" clones one into a custom dashboard (F8) that the user then owns.

The rail is the app's only navigation: the user's own dashboards (F8) sit below a divider in the same list, so there is one place to be, not two stacked rows of tabs. Views are a growing list and a window is wider than it is tall, which is the wrong way round for a horizontal strip that was already scrolling sideways; standing it up spends width the panels were not using and gives back the height they were. The actions that belong to a selected dashboard — export, import, delete — sit under the list they act on rather than in a toolbar that would be empty on every standard view.

The fight list keeps the opposite edge, and only on the views it does anything for. It is a time-frame selector (F7), so it appears wherever panels report over a time frame — which is every view except **Mobs**. Mobs is what this server's mobs are worth, learned across every kill ever seen; every click in the fight list would change nothing on screen there, so the panels take the width instead.

Window and span are presentation, not properties of a panel, so no panel definition carries them: there is exactly one default (`DEFAULT_CHART_SETTINGS`) and every chart in the app starts there. The top bar owns it — a control beside the version number — and changing it pushes down to every chart, Summary's DPS chart included. The setting persists across restarts.

Individual charts can still deviate: each time panel repeats the same controls in its header, with "apply to all" to put the rest of that view on its footing. A deviation lasts until the parent setting changes, which clears it rather than leaving some charts silently behind. Both ladders are multiples of the panel's bucket width, so a minute-bucketed chart offers minute-scale windows rather than the 1-second chart's seconds.

The span is the query, not just the picture: a whole-log panel is scoped to the span being viewed, so a total or a table beside a chart counts exactly the seconds the chart draws. Span "fit" means the whole log. Time charts fetch one extra rolling window of history beyond the span so the mean is warm at the left edge; that history sits outside the drawn axis.

Panels keep `bucketSeconds`, which is a different thing: it is a query parameter deciding what the server aggregates, not how the result is read.

Loot carries the same data read from both ends, side by side, because "what dropped this item" and "what does this mob drop" are the two questions a loot log gets asked and neither answers the other. The by-mob half is the one panel in the app that is not a plain reading of its own query: a drop rate needs a denominator the loot source does not carry — how many of that mob died — so it joins loot rows to a kill count from the death source over the same scope. That is why it is a viz of its own (`droprate`) rather than a table with extra columns. The join is case-insensitive, because the loot grammar keeps a corpse's name verbatim (`a bandit`) while the death grammar normalizes it (`A bandit`); loot's `target` dimension is inconsistent with every other source's until that is fixed at the parser.

- AC: The standard views cannot be edited, deleted or exported in place; "customize a copy" produces an editable dashboard and leaves the standard view unchanged.
- AC: Expanding a mob on the Loot view shows each item's drops per kill, over the same time frame every other panel reports on.
- AC: A fresh profile opens every time chart — Summary's and every standard view's — on the same window and span.
- AC: Changing the top-bar setting moves every chart in the app, discarding per-panel deviations.
- AC: Every panel in a view reports over the same time frame — narrowing the span lowers the totals and tables, it does not merely crop the charts.
- AC: Changing window or span on one chart and pressing "apply to all" moves every other time chart in that view to the same setting.
- AC: Time charts draw one continuous line — a bucket with no events reads as zero, not a hole — so idle stretches sag to the axis instead of fragmenting the chart. Ranges too large to fill point-by-point fall back to breaking on long gaps rather than degrading.
- AC: The chosen standard view survives a restart.
- AC: The fight list is absent on Mobs and present on every other view, including a user's own dashboards.

### F23. Stance analysis

What did switching stances actually buy you? Stances are a state the log announces and never times ("You assume a defensive stance."), so the durations are derived: switches are mutually exclusive, and pairing each with the next tiles the session with spans that have no gaps and no overlaps. See [log format §3.9a](../domain/eq-log-format.md) and [metrics §4](../domain/metrics-and-aggregation.md).

`stance` is a query **dimension**, not a bespoke report — so it composes with every source, viz and filter the query model already has, and a user can put "healing by stance" on a dashboard of their own without the app shipping that panel. The Stances standard view is then just a preset of panels over it.

The headline column is **per second held**, not DPS. Plain DPS divides by the time you were landing hits, which quietly refunds a stance every second it made you slower — precisely the cost you switched to weigh. Both columns sit side by side so the gap is legible rather than a matter of trusting one number.

The entry is conditional. Most servers and most characters never log a switch, and a permanently empty view teaches people to ignore the whole rail, so the session reports its switch count and the view appears only when there is one.

A stance is a fact about the log's own character: the parser can read your switches and nobody else's, because their client wrote theirs into their log. Records belonging to other players key to `(not you)` rather than being folded into whatever you were holding, and the view's panels are `ownerOnly` — a filter that resolves against whichever log is open, so a dashboard exported from one character still means "me" on another.

- AC: Stance names are read from the line's shape, so a stance the server adds later appears as itself rather than disappearing.
- AC: Uptime across the stances sums to 100% of tracked stance time.
- AC: Another player's damage is never attributed to a stance.
- AC: The Stances tab is absent on a log with no stance switches, and appears without a restart when the first one arrives.
- AC: Damage before the first switch is labelled `(no stance)`, never dropped from the parse.
- AC: A stance held across a logout accrues no time for the hours the player was away.
- AC: Durations read as durations — `3d 2h 30m 15s`, never `264615s` or `4410m`.

---

## P1 — Public v1

### F8. Custom dashboards

Create/duplicate/delete dashboards; add panels (table, line chart, bar chart, stat tile, heatmap); drag/resize on a grid; layouts persisted; export/import a dashboard as JSON.

- AC: A layout survives app restart; an exported JSON imports on another machine.

### F9. Death recap

For each player death: the final ~20 seconds of incoming damage, heals received, and buffs/debuffs landing, interleaved chronologically with running HP-relevant totals; accessible from summaries and the fight list.

F26's feed is the incoming third of this, already ordered and already scoped by
the time frame; what a recap adds is the interleaving with heals and buff
landings, and an entry point from a death rather than from a tab.

- AC: Recap for a fixture death matches the interleaving rules in the domain doc.

### F10a. Spell emotes resolved from the client's own files — **shipped (2026-08-16)**

The parser now reads `spells_us.txt` and `spells_us_str.txt` out of the game
install a log sits in (read, never bundled — the rule the maps and the
loot-filter file already follow) and uses them to resolve the per-spell emote
text that a buff landing or a received-buff fade prints instead of a name.
On the owner's log that is **93,791 fewer unrecognized lines (−26%: 355,303 →
261,512)** and 94k new `LandedEvent`s.

A shared emote does not name a spell: 39 spells say "Your wounds begin to
heal.", 556 say "&lt;name&gt; staggers.", and only 40% of these lines by volume
belong to exactly one. So the event carries the emote and the candidate count,
and names a spell only when it is unambiguous. `--no-spells` switches the
whole thing off; a log outside a game folder simply parses as it always did.

Still open here: the 173-column `spells_us.txt` also holds durations, class
levels and resists, but the columns are unlabelled and identifying them needs
its own evidence — that is the next slice, and it is what would give the
timeline true buff spans and class detection.

### F10b. Buff durations, and buff spans that end when they should — **shipped (2026-08-16)**

`spells_us.txt` columns 107 (duration formula) and 108 (cap, in 6-second
ticks) are identified and read, validated against durations measured from the
owner's own log (see [eq-client-files.md](../domain/eq-client-files.md)). With
them and F10a's landings, the timeline finally does what its own comments have
promised:

- **Received buffs are spans.** A buff landing opens one and its fade closes
  it, so a buff nobody watched being cast — including a debuff on a *mob* — is
  now drawn. On the owner's log a three-hour window holds 143 buff spans where
  it held only the handful they cast and saw fade.
- **A span with no fade ends when the spell says it would**, for spells the
  owner cast themselves, since the formula needs the caster's level and the
  log only ever states the owner's. Someone else's unfaded buff is still not
  drawn rather than given an invented end.
- A fade always beats the prediction: dispelled, zoned or overwritten is what
  actually happened.

### F10. Spell/cast analytics

Cast counts per player (casts, interrupts, twincasts), received-buff counts, spell damage breakdowns (DD vs DoT vs proc), resist tracking per spell and per NPC.

### F11. Hit distribution views

Histogram of hit sizes (crit vs non-crit) per player/skill; timeline density views.

**Half-shipped by F26** on the incoming side: mob attacks are already stored as
a log-spaced histogram and reported as a median with a p10–p90 band. What
remains is the outgoing half and drawing the distribution rather than quoting
three points off it.

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

The consent model is the feature, not the downloading. The default is to ask once per release, and every way of saying "no" states how long it lasts: *not right now* (until restart), *skip this version* (until something newer ships), *don't ask again for vX.Y.Z* (until the user is on a different build), or *always update automatically*. Preferences persist server-side, so they hold with no UI attached. **The prompt waits for a quiet moment** (2026-08-16): one found by the background check is held until the active session has no fight open and nothing has been hit for two minutes — never mid-pull; one the user asked for, by clicking *check for updates*, shows at once. *Update & restart now* is the primary button, since the prompt only appears between fights and a restart is back from the log cache in seconds; *update on exit* stays beside it.

- AC: An update is never applied mid-session — downloads stage quietly and install on exit.
- AC: Declining is always reversible; an explicit "check now" overrides every standing decline.
- AC: Nothing downloaded is executed unless it passes both Ed25519 and Authenticode verification.
- AC: Portable and unkeyed builds degrade to notify-only rather than pretending to install.

### F24. Gear snapshots — **withdrawn (2026-08-09, v0.9.4)**

Shipped in v0.7.0 and removed entirely. The feature read the dump produced by
`/outputfile inventory`, recorded each distinct version, and reported how each
gear set actually played.

It was withdrawn for **trustworthiness**, not for cost or complexity. EverQuest
records equipped gear nowhere and neither a swap nor an equip produces a log
line, so the app could only ever know what the player last remembered to dump by
hand. Every number it showed was therefore "true as of whenever you last typed
the command" while looking exactly like a measurement — and the feature's own
mitigations (fights-since-last-proof, "gear unknown" for older frames) were an
admission that the underlying signal could not be relied on. A panel that has to
keep apologising for its data is a panel that should not be presenting it.

Removed: the tab, the gear-change marks on time charts, `GET /api/sessions/{id}/gear`,
the `gear` SignalR event, `GearWatcher`/`GearStore`, the inventory-dump parser,
`--gearRoot`, ADR-011's design, and the inventory file-format doc. Snapshots
already written to `%AppData%\EQDeeps\gear\` are left on disk and simply
ignored; nothing writes there again.

**If this is ever revisited**, the blocker is unchanged and is not an
engineering one: there is no automatic source of equipped gear. Anything built
on the manual dump inherits the same problem, so a future attempt needs either a
game-side signal that does not exist today or an explicit design where the user
understands they are reading a hand-entered record rather than a parse.

### F25. Estimated mob health

How much health does that thing have? Nothing says — not the log, not the reference implementation, not any community dataset, because EQ Legends' instance difficulties are its own invention. But the log records every point a mob absorbs and the line where it died, so **health is measurable**: it is damage-to-death, minus however much the killing blow overshot by. See [ADR-012](../architecture/adr-012-mob-health.md) and [log format §3.9b](../domain/eq-log-format.md).

A mob is identified by **name, zone and instance difficulty** — not by name. Difficulty is read off the zone-entry line (`You have entered The Estate of Unrest 4 (Refined).`), and it is not a rounding error: the same mob's health climbs about ×1.15 / ×1.30 / ×1.50 at tiers 1–3 and roughly ×2.4 at tier 4. The open world and a tier-0 instance share a bucket, because the log writes them identically and they are the same content.

The other two instance settings — respawning and multiplayer — are **not logged at all**, and cannot be recovered. They are therefore not in the key, and the feature is built to say so rather than to paper over it: every number carries a p10–p90 band and a confidence grade, so a mob whose health depends on something unlogged presents as a wide band rather than a confident wrong answer.

Two corrections make the numbers worth trusting. Fights are keyed by NPC name, so two mobs of one name up at once become a single fight whose death banks both their damage — those kills are discarded in pairs, which tightens the median relative IQR from 0.34 to 0.24 while keeping 71% of the evidence. And the headline is a median rather than a mean, because the distribution is one cluster with a long right tail.

Learning **persists per game server**, not per character: a mob's health belongs to the world. The estimate for something fought last week is already there on tonight's first pull, which is the whole point of storing it.

It surfaces as analysis, not as a status bar — there is no live health meter, because the interesting questions are asked after the fight. The Mobs tab lists what has been learned; the fight list shows what share of the mob each fight accounted for (the honest read on "did we kill that, or get carried"); and the tier ladder puts one mob's every difficulty side by side, which is the question asked *before* making an instance and which no single row can answer.

- AC: The same mob at two difficulties is two rows with two numbers, never one averaged across both.
- AC: A mob killed once still appears, labelled Low, rather than being hidden until it is certain.
- AC: Re-opening a log, or leaving one open all evening, never inflates a kill count — the same kill banked twice is banked once.
- AC: Learned health survives a restart and is shared by every character on that server.
- AC: The demo log teaches the index nothing.
- AC: A fight that dealt a fraction of a mob's health says so, and one that dealt more than the median is not clamped to 100%.
- AC: With nothing learned yet, the panel explains what would populate it rather than showing an empty table.
- AC: The list leads with the most recently killed, not the best-evidenced — an index that accumulates for months would otherwise bury tonight's camp deeper the longer the app is used. (Changed by F26, which sorts its sibling list the same way.)

### F26. Incoming damage

What is hitting you, and what it hits for. See
[ADR-013](../architecture/adr-013-incoming-damage.md).

The Tanking view (F7b) already aggregates incoming damage over the time frame.
Two things are missing from it, in opposite directions: **the sequence
underneath** — "three parries, then a 900-point crush, then a death" is a story
no aggregation keeps — and **the memory above it**, since "how hard does a dar
ghoul knight in Old Guk tier 3 hit" has a stable answer the app re-derived every
session and then forgot.

So the Incoming sub-tab is two readings of one stream. The **feed** is the last
few hundred swings in the order they landed, avoided ones included, over the
app-wide time frame. It is the one view in the app that is deliberately not a
QuerySpec, because its subject is the ordering and every viz aggregates that
away. The **profiles** are what the server has learned across every log ever
opened against it.

A profile is keyed on **(mob, zone, difficulty, defender level)** — not on the
mob. How hard something hits is a fact about a *pairing*, and pooling a
level-40's incoming damage with a level-60's yields an average describing
neither. Levels come from the owner's dings and from any /who that caught a
player unanonymous; a level the log never stated is its own bucket, labelled,
never the owner's.

On EQ Legends that axis doubles as a **loadout** axis, and has to: class
loadouts level independently, so one character is several levels at once and
each is a different class with different mitigation. Swapping is not logged
([log format §3.9c](../domain/eq-log-format.md)), which means there is no single
"current level" to filter by — the level control shows every level by default,
orders them by which was played most recently, and says how many rows any
narrowing hides.

The headline figures are **melee only**. On a real log a forsaken revenant lands
209 punches averaging 66, 752 damage-shield ticks averaging 15, and four nukes
averaging 582 — pooled, "average hit 35", which is true of none of them. Spells
and shields are in the totals and broken out per attack.

Every number carries a p10–p90 band and a confidence grade, as mob health does,
but graded differently: a mob's melee has a real four-fold range, so spread is
the answer rather than the doubt. Confidence is graded on how many swings back
it and whether the defender's level was known at all.

- AC: The same mob at two defender levels is two rows with two numbers, never
  one averaged across both — and a row whose defender level was never
  established says so rather than borrowing one.
- AC: A character who plays several class loadouts sees all of them by default.
  No filter hides rows without saying how many and offering one click to
  restore them — a silent default filter is indistinguishable from missing data.
- AC: The feed shows misses, dodges, parries and blocks alongside the hits, in
  log order, and says how many it is not showing.
- AC: Re-opening a log, or leaving one open all evening, never inflates the
  tally — the same fight banked twice is banked once.
- AC: Learned profiles survive a restart and are shared by every character on
  that server at the same level.
- AC: A group member who never speaks, joins a raid or appears in a /who is
  still counted as a defender; a mob hitting another mob never is.
- AC: The headline hit size describes swings, not a damage shield averaged with
  a backstab; each attack is readable on its own.
- AC: Avoidance rates are stated as "of the swings the log accounted for" —
  ripostes are not in the log as attempts and the view does not pretend
  otherwise.
- AC: The demo log teaches the index nothing.
- AC: With nothing learned yet, the panel explains what would populate it rather
  than showing an empty table.
- AC: Both the feed and the profile table lead with the most recent thing —
  what you are fighting now, not what you have the most evidence about — and
  the profile table shows the instant it is sorted on, dated well enough that
  two rows from different days cannot read as out of order.

### F27. Zone maps — **shipped (2026-08-15)**

Explore the zones and how they join up, in the app. See
[ADR-016](../architecture/adr-016-zone-maps.md) and
[map format](../domain/eq-map-format.md).

The material is already on the player's disk: a stock install ships 1904 map
files carrying both the geometry and, in their labels, the zone connections.
They are **read from the EverQuest install, never bundled** — the community map
sets are freely distributed but not licensed for redistribution, players edit
their own copies, and the set is ~100 MB against a much smaller installer.

Three surfaces. A **Map destination** on the navigation rail: pick a zone,
pan and zoom, toggle the up-to-four layers a zone is split across, and click a
`to <Zone>` point to travel to that zone's map. A **world graph** of zones and
their connections, with fewest-zones routing between any two. And a **compact
dashboard panel**, for "where am I" beside a parse.

A map is deliberately **not a QuerySpec** — no records, no time frame, no
metric, nothing for the app-wide time control to act on — which is why it is a
rail destination rather than a panel type forced to carry a query it never runs.

The hard part is a join that does not exist. The log says `You have entered The
Estate of Unrest.`; the map file is `unrest.txt`; **nothing in an EverQuest
install connects those two names**, because the server tells the client its
short name on zone-in and the client therefore never needs a table. Short names
are historical abbreviations, so string matching alone resolves 108 of 581
zones. The maps' own connection labels close most of the rest: they name
neighbours in *display-name* space, so a known zone identifies its unknown
neighbours, and the pairing is confirmed when they name it back.

The shipped table is **268 rows covering 128 of the 133 zones a stock client
ships a map for**, each marked with how it was arrived at — matched, deduced, or
hand-written. It is knowingly incomplete, and that is safe: an unknown zone
resolves to no map and the user picks one, which is the same escape hatch that
corrects a pairing this table gets wrong.

**Eras** (issue #57, 2026-08-15). A stock install ships every expansion's maps
whether or not the server has unlocked them, so on a classic-era server the
World view drew and routed through content that does not exist yet. Nothing on
the disk says what era a server is running, so the era is **chosen by the
player** in the World view and remembered; zones from later expansions are
hidden and never routed through. Which expansion a zone is *from* comes from
the client's zone-id bands, validated against the file and checked in as two
more columns of the table with their provenance — a lower bound, and a zone
the table cannot place is shown rather than hidden.

- AC: A zone the table does not know is an invitation to pick a map, never an
  error or an empty screen with no explanation.
- AC: Where a zone has more than one map — a revamp beside its classic version,
  like `freportw` and `freeportwest` — both are offered rather than one being
  guessed, and the choice can be made to stick with one press (which drawing is
  right depends on the server: a classic-era server has the old Freeport).
- AC: Every display name in the shipped table is one the client itself uses,
  checked against `Resources/ZoneNames.txt` rather than trusted.
- AC: A hand-written pairing is distinguishable in the UI from a derived one,
  because only the second kind is verifiable.
- AC: No machine without EverQuest installed shows a broken Map view; it shows
  an explanation and a way to point at a maps folder.
- AC: A route the labels cannot support is reported as "no route known", never
  invented from partial data.
- AC: The era is chosen by the player and never guessed from the log; with no
  era chosen the World view and routing behave exactly as before.
- AC: With an era chosen, no zone from a later expansion is drawn or routed
  through; a zone whose era the table cannot determine is shown, not hidden.
- AC: The era derivation is committed as data with the script that produced
  it, and the id bands are written into the map format doc with their evidence.
- AC: The largest zone (`everfrost`, 26,383 segments) pans and zooms without
  dropping frames.
- AC: Maps are drawn legibly on the app's dark surfaces despite the files'
  colours having been chosen for the client's light background, and the file's
  own colours are never rewritten.

### F28. Log cache — **shipped (2026-08-16)**

A log is parsed once. Issue #59; see [ADR-018](../architecture/adr-018-log-cache.md).

Every open used to read the whole file through the parser and rebuild every
record in memory, and the file only grows: for a 2 GB log — an ordinary
raider's — that was ~19 s and ~5 GB of memory, every launch. Now the parsed
records are written to a per-log cache under `%AppData%\EQDeeps\cache\` as
soon as backfill completes (and once a minute for the live tail, and on
close), and the next open restores them from disk and starts the parser at
the byte offset where the cache ends. Alongside, every repeating string in
the records — names, spells, zones — is pooled to one instance per session,
which is where half the memory was going.

The cache holds records only. Fights, identity, and everything above the
parser are rebuilt by replaying the records through the same path a parsed
one takes, so a resumed session and a cold one are the same session — and a
change to fight logic never invalidates a cache, while a change to the parser
invalidates all of them (the file is stamped with the parser build that wrote
it). Whether the log is still the log the cache describes is decided by
hashing the 64 KB before the resume offset, never by name or size.

- AC: A second open of an unchanged log restores every record from the cache
  and re-parses nothing; a grown log re-parses only the growth. Measured: 512
  MB, 3.6 s → 1.6 s; 2 GB, ~18 s → 5.9 s; resident memory halved.
- AC: The restored session is indistinguishable from a cold parse of the same
  file — records, fights, counters — and this is asserted by test, not
  assumed.
- AC: A log that has been trimmed, replaced, or truncated and regrown is
  never resumed from a stale cache; the cache is dropped and rebuilt.
- AC: A truncation or rotation while the session is open restarts the cache
  from the new content; the next open matches a cold read of the new file.
- AC: An upgrade that changes the parser invalidates every cache, with no
  one having to remember to bump anything.
- AC: Nothing about the cache can fail an open, a session, or the app: a
  corrupt file, a full disk, a second session on the same log, an archive,
  all degrade to parsing as before.
- AC: The cache is recomputable — deleting the folder loses nothing — and it
  sweeps itself: caches for logs that are gone, untouched for 60 days, or
  written by an older parser build (all but the newest other build per log,
  so a dev build and the release coexist warm) are deleted on start-up.
- AC: `--cacheRoot` redirects it like every other store, and every test
  harness passes it.
- AC: The World view's graph is not rebuilt from the map files on every
  launch: each map's labels are cached and validated per file, so the first
  click costs a stat per file rather than a read (measured 2.4 s → 0.35 s
  on the owner's install), an edited map re-parses only itself, and the
  graph is identical to one built from the files.

---

### F29. Item lookup — from a name in the log to the page that explains it (issue #62) — **shipped (2026-08-16; icons open)**

When an item is looted or named in chat, get to "where does it drop, which
quest wants it" without alt-tabbing to a search box. See
[ADR-019](../architecture/adr-019-reference-lookup.md) and
[eq-client-files.md](../domain/eq-client-files.md) for what discovery found:
the client ships no item data, and on EverQuest Legends the log carries no
item-link payload — a name is all there is — so the app links **out, by
name**, to the sites that know the rest, and learns ids only from the
player's own files.

Acceptance:
- Every item name in the Loot view (and the mob under it) carries a lookup
  door: an arrow on hover, a menu of the reference sites for this log's world,
  each opening a browser tab. *(shipped)*
- The same door beside a mob's name **wherever one is shown** — the fight
  list, the Summary's by-target rows, Incoming's feed and profiles, the death
  log, the Mobs table, and mobs in player-shaped columns (a death's victim)
  — so a lookup starts from whatever view is open. **Chart names too**: a
  click on a category-axis label (Hardest hitters, Timeline actors) goes to
  the usual site; right-click on an axis label, legend entry or bar opens the
  menu. *(shipped)*
- Which sites: guessed from the install ("EverQuest Legends" → EQL Wiki, Gnoll
  Guard, EQLBase, Allakhazam; otherwise the live set) and overridable per
  install in Settings, persisted in the `ui-settings` document. *(shipped)*
- Legends' ` +N` and ` (Exaltation)` decorations are stripped before asking a
  site. *(shipped)*
- **Item registry**, per server (`%AppData%\EQDeeps\items\<server>.json`,
  `--itemRoot`): every item the logs have looted, sold or bought, meeting the
  ids from the player's own client files — `userdata\LF_<Char>_<server>.ini`
  and the `/outputfile inventory` dump, read from the install the log lives
  in, re-read when they change, never copied. `GET /api/sessions/{id}/items`,
  `…/items/resolve?name=`. *(shipped — 1,150 items / 528 numbered on the
  reference log)*
- Ids light up the id-addressed sites (EQLBase, EQResource, Lucy) on the
  lookup menu: the door asks the registry on hover and on open, name links
  show at once, id links join when the answer lands. *(shipped)*
- **One click to the usual site**: a plain click on the arrow opens the
  world's default site; right-click opens the menu, where a star on any site
  makes it the default (per world; also a select in Settings). A default that
  needs an id falls through to the first name-addressed site until the id is
  known. *(shipped)*
- **Item feed** at the top of the Loot view (viz `items`, also available to
  custom dashboards): every item looted, sold, bought or **named in chat** in
  the time frame, newest first — who, where (corpse, merchant, channel), the
  chat line — each with its door. Chat mentions are a dictionary match
  against the registry (Legends writes no link markup): whole words, longest
  name first, one-word names only in their own case and not inside a
  capitalised phrase. `POST …/items/mentions`. *(shipped; F15's chat archive
  was not needed)*
- Merchant sales and purchases are parsed (`MerchantEvent`), and the loot
  grammar now takes `an` and stack counts on the `--…--` form — both had been
  dropped. *(shipped)*
- Item icons from the client's `dragitem*.dds` sheets (the registry already
  carries the icon id). *(open — needs a DDS decoder on the server)*
- Room for other servers: a new world is one entry in `providers.ts`; the
  era is not duplicated here (it lives with the maps).

### F30. Bestiary — every mob in the game, searchable (issue #51) — **shipped (2026-08-16)**

A rail view under World: search all ~5,300 mob names EverQuest Legends has,
and for any of them see level, health, AC, damage, race, class, faction,
respawn, where it spawns and what it drops — **beside what your own logs
measured**. See [ADR-020](../architecture/adr-020-npc-reference.md).

The original plan was a registry built from the log. It was measured first
and abandoned on the numbers: the owner's 118 MB log yields 580 mobs killed
(1,061 names mentioned at all), 75% of them with a /consider level, against
5,349 names a reference site lists — ~24% coverage, with most rows carrying
little but a name. What a log *can* say that no site can is what a mob took
to kill, on this server, at this difficulty, and that is F25 already.

So: reference data for breadth, our measurements for truth, both labelled.

Acceptance:
- Search every listed mob by name; results show each level variant. *(shipped)*
- A mob's page shows the listed stat block, spawn zones and loot table with
  drop rates, each item carrying its lookup door (F29). *(shipped)*
- Beside it, what this server's logs measured for that name — damage to kill
  per zone and difficulty tier, kill count, the levels you consed, and the
  ratio to the listed health. *(shipped — the owner's data shows ×0.93 open
  world and ×2.09 at the Fused tier for the same mob, which is F25's thesis
  in one row)*
- Matching a log name to a listing uses the session's /consider levels, since
  one name is listed at several levels, and says whether a level corroborated
  it rather than implying certainty. *(shipped)*
- **Nothing is fetched until the view is opened**; the index loads on open
  (opening the view is the ask), revalidates at most once a day by ETag, and
  Settings → "Look mobs up online" and `--no-reference` switch it off
  entirely. *(shipped; 2026-08-17 moved the index load from first search to
  open, which is what fixed a header that said "loading…" over nothing)*
- Data is never bundled — EQLBase states no licence — and every screen showing
  it names and links the source. *(shipped)*
- The view opens on something: the mobs this server's logs have killed, most
  killed first, and level bands to browse the rest of the world; the page for
  a mob leads with listed health beside measured damage-to-kill and listed
  damage beside what it actually hit you for (F26), with a con colour against
  a level you can change, and says plainly when nothing has been measured.
  *(shipped 2026-08-17)*
- **Both ways to the map (F27):** a listing's zone and every other zone the
  name stands in open the Map with the mob's spawn points drawn; the Map's
  Mobs tab lists who stands in the zone on screen — the log's kills marked —
  and searches where any mob stands; each hop leaves a crumb back. The zones
  come from the listing ids alone (ADR-020, decision 6), so "where does it
  stand" costs no fetch. The same rail stands on the World view: a zone
  click frames it in the graph, and a mob under the pointer lights every
  zone it stands in. *(shipped 2026-08-17)*
- Loot sorted by chance, with the lines the item registry (F29) has seen
  looted marked. *(shipped 2026-08-17)*
- Open: item icons (the icon id is already in the data), and using the same
  index to seed F21's level-normalized DPS.

## P2 — Later

- **F15. Chat archive & search** — persist chat by channel/player with full-text search and date ranges.
- **F16. Loot & random-roll tracking** — looted items, currency splits, /random winners.
- **F17. Log archiving** — scheduled or zone-triggered rotation/compression of giant EQ logs (the game appends forever; multi-GB files are normal).
- **F18. ADPS awareness** — track crit-modifying buffs to contextualize damage spikes (reference: adpsMeter data in old app).
- **F19. Report export** — HTML/CSV export of any view; shareable fight report bundles.
- **F20. Trigger system** — GINA-style pattern alerts (explicit non-goal for v1; keep the ingestion layer's line stream subscribable so this can attach later).
- **F21. Mob-normalized DPS context** — cross-fight DPS aggregates are skewed by level differences and mob mitigation. Ship/derive an NPC-stats database (level, class, AC/mitigation tier per zone/era) and use it to annotate aggregate DPS with expected upper/lower bounds per target, so "average DPS" comparisons across different content are honest. (Owner request, 2026-08-01; aggregate selection UI ships first and accepts the skew.) **Half-unblocked by F25:** per-mob size is now measured, and con lines (PR #5) supply level, so the external dataset is no longer needed for either. **F26 adds the other side of it** — how hard a mob hits, measured per defender level, which is the mitigation half of "was that DPS good for this content".

---

## Cross-cutting requirements

- **Latency:** file-append → visible update ≤ 250 ms target (old app: 1–2 s).
- **Backfill throughput:** historical load should saturate disk read, not parser — target ≥ 100 MB/s on typical hardware; a 1 GB log's last raid night loads in seconds. (Old app parses a full file in minutes on large logs.)
- **Scale:** 54-player raids, hundreds of combat lines/second burst, logs up to several GB, fights lasting 10+ minutes, sessions monitoring 3+ characters.
- **Correctness:** parsing fidelity against the fixture corpus (see HANDOFF.md verification section) is a release gate.
- **Licensing:** all dependencies MIT/Apache-2.0/BSD-compatible. The fixture corpus is derived from EQLogParser's parser tests (Apache 2.0) and **its attribution in `NOTICE` is an obligation, not a courtesy** — it stays as long as those fixtures do. No data files have been copied from it and none are wanted: reference data comes from the player's own game install (`docs/domain/eq-client-files.md`) or is fetched at their request and attributed on screen (ADR-020).
