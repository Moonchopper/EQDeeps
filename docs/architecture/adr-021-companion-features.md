# ADR-021: Four things a neighbouring app does well — what we take, what we cannot, and in what order

Status: accepted (2026-09-20). Scope: the programme behind features F31 (raid
targets), F32 (Plane of Sky tracker), F33 (gear) and F34 (overlays). Each gets
its own ADR when its turn comes; [ADR-022](adr-022-raid-targets.md) is the
first. Amends the non-goals in [vision.md](../product/vision.md), reopens
[ADR-011](adr-011-gear-snapshots.md) under the clause it left for that, and
extends [ADR-020](adr-020-npc-reference.md) from mobs to items.

## Context

The owner uses [EQ Legends Companion](https://github.com/jmoyers/everquest-companion)
beside this app and asked for four of its features here: **Raid Targets**,
**everything under Plane of Sky**, **the Gear tab**, and **the overlays**. It
is an Electron app with a Rust log engine, built for the same game and the
same log, so it is the closest prior art this project has had. It was read end
to end for those four features on 2026-09-20; what follows is what that found.

Two facts shaped everything else.

**It is not ours to copy from.** The licence is FSL-1.1-MIT — source-available,
converting to MIT two years after each release — not MIT. Nothing in it can be
relicensed into an MIT repository today: not code, and not the data it
bundles, which is the larger part of what makes its features work (an 8.7 MB
item table and a 95-quest Plane of Sky table scraped from the EQ Legends wiki,
a respawn table from the same place, raid-boss portraits from the Project 1999
wiki). The wiki page checked states no licence, which by
[ADR-020](adr-020-npc-reference.md)'s reasoning is absence of permission, and
nothing read in the companion's repository records one being given.

**Two of the four were written down as things this app is not.**
[vision.md](../product/vision.md) listed "No in-game overlay windows" and "Not
a … gear planner" as v1 non-goals, and gear had been shipped once and
withdrawn ([ADR-011](adr-011-gear-snapshots.md)).

## Decision 1: a behaviour authority, under a stricter rule than the clean-room one

The companion is read the way `d:\git\EQLogParser` is read — to settle what a
feature *does* and which log lines it stands on — and the answer is written
into our docs and implemented fresh. The difference is that EQLogParser's
fixtures could be taken under Apache-2.0 with attribution; **nothing can be
taken from the companion at all**, data included. A brief for any of these
features points an implementer at our ADR, never at the companion's source.

## Decision 2: the two non-goals are lifted

Owner's call, 2026-09-20. Overlays and gear come into scope; vision.md is
amended in the same change. **Triggers, audio and alerts stay out** — the
companion's "raid target defeated" sound and its alert banner overlay are not
part of this, and neither is anything that would need them.

## Decision 3: the order is cheapest-and-cleanest first

1. **Raid targets (F31).** Pure log, no new data source, no vision change, and
   the pieces exist: deaths and experience are parsed, the zone timeline is
   built, difficulty is split off the zone line. What is missing is making
   zone and difficulty things a query can group by.
2. **Plane of Sky tracker (F32).** Needs two new grammars (item turn-ins,
   item destruction), a held-item ledger, a quest table, and item details
   fetched from the reference site.
3. **Gear (F33).** Reuses F32's item fetch. Third because it needs the most
   of someone else's data and an ask made of its author first.
4. **Overlays (F34).** Largest, and the one with an unverified technical
   question at its root. A spike precedes any design.

Each is independently shippable; none is a prerequisite for a release.

## Decision 4: where the data for each comes from

This is the part the companion solved by scraping and bundling, and the part
that has to be solved differently here.

| Needs | Source | Why |
|---|---|---|
| Which mobs are raid targets | **Hand-authored**, checked in as data | Thirty-odd names and their zones are facts about the game, not anyone's compilation. The companion's list is hand-typed too. |
| Boss portraits | **None** | They are another wiki's images. An initial in a tile costs nothing and owes nobody. |
| Which items each Sky test wants, and what it gives | **Not decided — the owner's to settle when F32 starts.** Recommended: hand-authored, checked in as data, like `zones.tsv` | The one reference site we may fetch from publishes quest steps as HTML only, and labels its own Sky steps "classic-script, not yet confirmed in Legends", with item ids it cannot name. Scraping a page is what [ADR-019](adr-019-reference-lookup.md) already declined. Hand-authored facts can be checked against the owner's own turn-in lines and against the item data's `questHandins`. The alternative is asking a site's author for permission to take theirs. |
| Item stats, drop sources, quest uses | **Fetched from EQLBase, cached, attributed, never bundled** — ADR-020 extended to items | The site publishes items exactly as it publishes mobs: `/data/items/<id÷1000>.json`, static, sharded. Each item carries its stats, slot/class/race masks, `drops` (mob, zone, chance), `questHandins` and `questRewards`. The index this app already downloads lists 13,813 of them; today only its mob and zone rows are read. |
| What the character holds | The log's loot, turn-in and destroy lines, and the `/outputfile inventory` dump | `InventoryDump` already parses the dump with per-item counts (it survived F24's removal as a feeder for the item registry, ADR-019). |
| What the character wears | The `/outputfile inventory` dump, and nothing else | Still the only source there is — see Decision 5. |

**The ask.** A Sky tracker touches a few dozen items and fetches a handful of
shards on demand, which is the pattern ADR-020 already accepted. A gear
*browser* is different in kind: searching every equippable item means having
the whole item corpus, roughly 10 MB estimated from one measured shard
(392 KB for 537 items). That is still a GET for static files the site invites
anyone to take, but it is a bulk read of someone's whole dataset rather than a
lookup, and ADR-020 already says asking the author is worth doing. **F33 does
not start until that ask has been made**; if the answer is no or never comes,
the browser is scoped to the items this server's logs and the player's files
have named (the F29 registry), which needs no bulk read.

Every rule in ADR-020 carries over unchanged: nothing fetched until the user
opens the view that needs it, `--no-reference` and the Settings switch turn it
all off, the source is named wherever its data shows, a failed fetch changes
nothing else in the app, and tests run against a fake.

## Decision 5: gear returns as a hand-kept record, and says so

ADR-011 withdrew gear because a dump-derived figure was shown with the
authority of a parse. It left one door: *"an explicit design in which the user
understands they are reading a hand-maintained record rather than a parse —
and in which nothing derived from it is presented beside measured numbers as
though it were one."* F33 goes through that door and no further:

- The **item browser** — search by slot, class, stat — never needs to know
  what is worn, so ADR-011 has nothing to say about it.
- **"Compared with what you are wearing"** and **"owned"** read the inventory
  dump. Everywhere either appears, the dump's age is on screen beside it
  ("from your inventory file, 6 days old"), and with no dump the comparison
  is absent rather than empty.
- **Nothing from the dump reaches a parse.** No gear marks on time charts, no
  "this set did N DPS", no gear dimension in the query model. Those are what
  was removed, and they stay removed.

The owner chose the dump-based comparison over a browser alone (2026-09-20).

## Decision 6: an overlay is a panel, popped out — after a spike says it can be

The companion feeds its overlays through a purpose-built bridge from its
engine. This app already has the feed: one SignalR connection and a query
engine serving panels. So an overlay here is **a dashboard panel shown in a
second, chromeless, always-on-top window** on a route of the same SPA — no new
rendering path, no overlay-specific data code, and any panel a user has built
can become one. That is the design to aim for, and it is not yet a decision,
because its foundation is unverified:

**The spike.** The shell is a WinForms `Form` hosting WebView2
([ADR-009](adr-009-windowed-shell.md)). Always-on-top, click-through and
never-take-focus are extended window styles and are expected to work.
**Whether a WebView2 can be seen *through*, per pixel, onto the game beneath
it is not known** — WebView2 supports a transparent background, but that may
reveal only the host form. If it cannot, the fallback is whole-window opacity,
which is a different and lesser product (a dimmed rectangle, not floating
bars), and the owner should see both before a design is written. The spike is
one throwaway window and an afternoon; F34's ADR follows it.

**Traps the companion already paid for.** These are Windows behaviours, not
Electron ones, so they are ours too, and its notes on them are the most
valuable thing in its repository:

- Decide whether a window can take focus **when it is created**, and never
  change it while it is visible. Toggling it hands the foreground to the next
  window down — the game — and alt-tab then keeps returning there.
- **Park** an overlay that should disappear (opacity zero, input off) rather
  than hiding it. A hidden window stops compositing and comes back showing a
  stale frame.
- **No mouse-forwarding hooks.** Theirs put every mouse event on the machine
  through the app's message loop; a 30 ms stall froze the cursor and in-game
  mouselook, and Windows silently removes a hook that is too slow, so it
  decayed rather than failed.
- A pixel cannot both scroll and be clicked through, so a scrollable overlay
  needs a grip that is not click-through.
- EQ Legends' "Fullscreen" is really borderless, so overlays work over it;
  exclusive fullscreen cannot be overlaid by anything.
- Keep the rectangle the user chose; draw the one that fits. Undocking a
  monitor must not rewrite a stored position.

## What was considered and not done

- **Taking the companion's data with attribution**, as was done with
  EQLogParser's fixtures. Those were Apache-2.0; this is not, and its data is
  a third party's besides.
- **Scraping the EQ Legends wiki ourselves** — argued against here, though
  the Sky table's source is formally open until F32 (Decision 4). It would
  reproduce the companion's approach exactly, including its failure: a wiki
  restructure in August 2026 renamed every class heading and their scraper
  overwrote 95 quests with an empty file. It also needs about 250 lines of
  hand corrections on top, so it does not escape hand-authoring; it adds a
  scraper to it.
- **A general quest tracker.** The companion's is bespoke to Sky and that is
  the right size. The quest table's *shape* is general (giver, items wanted,
  reward) so a second quest line is more rows, not a new design — but nothing
  is built for quests nobody has asked to track.
- **Stat weights and best-in-slot suggestions.** The companion has none
  either: its "upgrades" are a sortable table and a comparison card. A scoring
  engine would be inventing opinions about this game's itemisation.
- **Respawn timers.** In the companion these are a separate opt-in feature,
  not part of Raid Targets. This app already shows a mob's listed respawn in
  the Bestiary; a countdown is a timer, and timers stay with triggers, out.

## Consequences

- vision.md loses two non-goals and features.md gains F31–F34, planned.
- The investigation turned up a log-format fact unrelated to any of the four:
  **raid zones print `- Solo` or `- Group` on the zone line**, which the
  domain doc said was never logged. Recorded in
  [eq-log-format.md](../domain/eq-log-format.md) §3.9b; the parser change is
  part of F31 because raid targets are where it bites first.
- ADR-020's "first feature whose content is partly not ours" becomes three
  features. The Settings switch that turns the Bestiary off turns all of it
  off, and should be worded for that when F32 lands.
- The reference store learns a second kind of shard. Item shards join
  `%AppData%\EQDeeps\reference\` under the same `--referenceRoot`; no new
  flag, because it is the same store and the same cache.
