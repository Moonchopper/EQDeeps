# ADR-019: Names go out to the sites that know them; the app keeps its own bestiary

Status: proposed (2026-08-16), first slice shipping. Scope: issues #62 (item →
wiki/quest) and #51 (NPCs, searchable); `ui/src/lookup/`, the Loot and Mobs
tables, Settings; later a per-server NPC store and a Bestiary view.
Companion domain doc: [eq-client-files.md](../domain/eq-client-files.md).

## Context

Two asks arrived together. #62: when an item is linked in chat or looted, get
from the name to "where does this come from, what quest wants it" without
alt-tabbing to a search box. #51: NPCs — the app should know them, and you
should be able to search them. Both are about **reference data**, which is the
one kind of data this app has never had: everything it shows today it computed
from the log.

Discovery (2026-08-16) settled where reference data can come from:

- **Not the client.** The install ships no item, NPC, quest, loot or merchant
  tables and never has — those are server-side. What it does ship is listed in
  the client-files doc: a complete spell DB, faction and expansion names, the
  Hunter achievements (≈2,150 named NPCs with their zone), the Brewall map
  labels (named roamers, merchants, ground spawns, with coordinates), and two
  *player-written* files that number the items the player has met
  (`userdata\LF_<Char>_<server>.ini`, `<Char>_<server>-Inventory.txt`).
- **Not the log, for ids.** On EverQuest Legends the log carries no `\x12`
  item-link payload — a linked item is plain text — so a chat line yields a
  name and nothing else. (Live clients do write the payload; the door is left
  open, see below.) The log *is* the authority on which NPCs exist and where:
  every mob fought, considered, killed or heard is in it with a zone around it,
  and F25/F26 already persist that per server.
- **The web, by name.** Legends has its own community stack — the EQL Wiki
  (MediaWiki: `index.php?search=<name>` lands on an exact title or on results),
  Gnoll Guard (`/search?q=`, `/items/<Name>`), EQLBase (id-addressed pages;
  its ids are the game's — 407/478 of the loot-filter file's names match by id
  exactly), EQ Legends Tools — and each of the other worlds has its own
  (Project 1999 wiki, Allakhazam, EQResource, Lucy). Both Legends databases
  ship a **companion log reader** (Gnoll Guard's Windows app, "EQLBase
  Collector") that mines the player's log for the same facts this app
  already parses; that is the prior art, and it confirms the log is where the
  crowd-sourced data comes from.

Two constraints from the owner: keep the MVP to Legends but **leave room for
other servers with other eras unlocked**, and don't over-invest in that room.

## Decision 1: lookup is a link out, by name, to a world's sites

The app never scrapes, caches or re-hosts a reference site. Beside a name it
puts a door: an arrow that opens a small menu of the sites that can address
that kind of thing, default on top, each a real `target="_blank"` link the
shell hands to the default browser (ADR-009). One extra click over a straight
jump, deliberately: the sites disagree about coverage — one has the quest,
another the drop rate — and the person clicking knows which they wanted.

The door goes **wherever a name is, not where the data happened to be a
table** — the owner's rule, stated the moment the first slice stopped at Loot
and Mobs: fighting a spiroc caller, you start from the fight list or the
Summary, not from a view you have to remember exists. So the fight list rows,
the Summary's by-target rows, the Incoming feed and profiles, and the death
log carry it too, and any new surface that names a mob or an item is expected
to. The install that decides the world is a React context (`LookupScope`) set
once by App for the active session, so a door in a memoised fight row deep in
a list needs no prop about game installs.

`ui/src/lookup/providers.ts` is the whole of the knowledge: a **provider** is
a URL template over a `LookupRef { kind, name, id? }`; a **world** is a named,
ordered list of providers. Providers that need an id (EQLBase, EQResource,
Lucy) return no URL until one is known and simply do not appear; nothing else
changes when ids arrive. Names are normalised on the way out (`lookupName`):
Legends' ` +N` upgrade rank and ` (Exaltation)` tag come off items, the loot
grammar's article comes off mobs.

## Decision 2: the world is a fact about the install, guessed then chosen

Which sites are right depends on which game the log came from, and one
machine can hold a Legends install beside live and a P99 client. So the world
follows the pattern ADR-016 set for map choices and eras: **keyed by install**,
stored in the document store (`ui-settings`, its first tenant, nested under
`lookup`), read-modify-write so it can share the document. Until the user
picks, it is *guessed* from the install's name on every read ("EverQuest
Legends" → Legends; anything else → live), never written — a guess that
improves later is not frozen into a file. Settings has one row: "Look things
up on", saying which install it is for and whether it is a guess.

That is the room left for other servers, and all of it: a new server family
is one more `LookupWorld` entry; a server that changes era changes nothing
here (the sites cover every era of their world) — the era already lives in
`map-settings` and the map view, and is not duplicated.

## Decision 3: ids come from the player's own files, and only enrich

Shipped in the second slice (2026-08-16). The server keeps a per-server
**item registry** (`ItemRegistry`, `ItemStore`, `%AppData%\EQDeeps\items\`,
`--itemRoot`) fed from two directions: the log — every loot, sale and
purchase, swept past a watermark on the session's tick so counts do not
tick again on replay — and the player's own client files,
`userdata\LF_<Char>_<server>.ini` and the `/outputfile inventory` dump, read
from the install the log lives in (never copied, as F27 does with maps) and
re-read when their size or write time changes. Names meet on
`ItemNames.Key` — base name (Legends' ` +N` and ` (Exaltation)` off),
case-folded — so the loot line, the filter file and a chat mention are one
row; a file's casing outranks the log's. The registry never gates a lookup:
the door asks `…/items/resolve?name=` on open, name-addressed sites are on
the menu at once, and the id-addressed ones join when the answer lands. On
the reference log that is 1,150 items, 528 of them numbered.

Chat mentions are found by `ItemMentionScanner`, a dictionary match against
the registry's names: whole words, longest name first at a position, any
case for multi-word names, own case only for one-word names and not beside
another capitalised word ("Horn" inside "Efreeti War Horn" is another item).
An item nobody on the server has looted, sold, bought or filtered is
invisible to it, and the feed's empty state says so. If a live log ever does
carry `\x12` payloads, decoding them is a third feeder into the same table,
not a new design.

Two grammar facts fell out of building it: the `--…--` loot form takes `an`
and a stack count, which the parser had dropped for the whole life of the
project, and merchant sales and purchases name items too (`MerchantEvent`,
kept apart from loot so vendored stacks do not count as drops).

## Decision 4: the app keeps a bestiary, and search is over that

For #51 the app's own record is the database. A per-server **NPC registry**
consolidates what is already known — F25 kill samples, F26 attack samples,
`ConsiderEvent` levels, `DeathEvent` victims, the identity registry's known
NPCs — into one row per name: zones seen, levels seen, health estimate, kills,
first/last seen; enriched offline from the install by the Hunter achievements
(named → zone, for mobs never met) and, on the map, by Brewall's labels. A
**Bestiary** view lists it with the same fuzzy search box every table has and
a lookup door on every row. Search is over rows already fetched, as F7b's
"a result table is a list you interrogate" already promises; a global search
box is not proposed until there is a second thing to search.

## What was considered and not done

- **Bundling a reference database** (item or NPC). There is none on disk to
  read, the community ones are someone else's work with no stated licence,
  and a copy goes stale the week it ships. Linking out costs nothing to keep
  current.
- **Fetching a site's search index at runtime** (EQLBase publishes a 1 MB
  `search-index.json` its own search box loads). It would give name → id for
  everything, but it is their bandwidth and their data; if their author says
  yes it becomes one more provider, not a change of design.
- **A chat view** to click links from. Chat is nowhere in the UI (F15, P2),
  and building it to hang one arrow on is the wrong order; the second slice
  surfaces *item mentions* (who, when, which item) instead, which is the part
  of "linked in chat" the ask was actually about.
- **Per-kind provider preferences.** One world per install is enough; the
  menu shows the alternatives every time.

## Consequences

- Slice 1 (this ADR's first commits): providers/worlds, install-keyed
  preference, the arrow-and-menu on Loot rows (item and mob), drop-rate rows,
  the Mobs table, the fight list, the Summary's by-target rows, the Incoming
  feed and profiles, and the death log (a mob-shaped name, or the killer of a
  player); the Settings row. No server change.
- Slice 2 (#62), shipped: the item registry, ids on the menu, and the
  **Item feed** at the top of the Loot view (viz `items`) — looted, sold,
  bought, named in chat, newest first, each with a door. Icons wait on a DDS
  decoder.
- Slice 3 (#51): NPC registry (persisted per server, F13-style snapshot of
  the identity registry alongside it) and the Bestiary view; achievements and
  Brewall enrichment behind it.
- Docs to keep honest: `eq-log-format.md` §3.1 gets the "no link markup on
  Legends" fact; `features.md` gets F29 (item lookup) and F30 (bestiary).
