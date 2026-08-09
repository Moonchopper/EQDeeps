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
snapshots; the disk read/write wiring remains), F15–F21. Release gate still
open: real-log validation against EQLogParser. Design decisions live in
`docs/architecture/adr-0*.md`.

**Update (2026-08-06):** F25 (estimated mob health) shipped — instance
difficulty is parsed off the zone line, kills are measured into a per-server
index, and the Mobs tab, the fight-list share column and the tier ladder read
it.

**Update (2026-08-08):** F26 (incoming damage) shipped — an Incoming sub-tab
carrying the raw feed of swings taken and a per-server attack index keyed on the
defender's level as well as the mob, zone and difficulty.

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

The consent model is the feature, not the downloading. The default is to ask once per release, and every way of saying "no" states how long it lasts: *not right now* (until restart), *skip this version* (until something newer ships), *don't ask again for vX.Y.Z* (until the user is on a different build), or *always update automatically*. Preferences persist server-side, so they hold with no UI attached.

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

---

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
- **Licensing:** all dependencies MIT/Apache-2.0/BSD-compatible. If any data files or fixtures are copied from EQLogParser (Apache 2.0), preserve attribution in a NOTICE file.
