# ADR-020: Mob details are fetched from a reference site, never bundled, and never load-bearing

Status: accepted (2026-08-16). Scope: issue #51 (F30, the Bestiary) — the
`EQDeeps.Core.Reference` parsers, `NpcReferenceStore`, the
`/api/reference/*` endpoints, the Bestiary view, and the Settings switch that
turns the whole thing off. Follows [ADR-019](adr-019-reference-lookup.md),
which linked out to these same sites; this one reads from one of them.

## Context

Issue #51 asked for NPCs, searchable. The obvious build was a registry fed by
the log — every mob you fought, with the levels your /consider lines reported
— so before writing it we measured what that would actually contain, against
the owner's own 118 MB log and their EverQuest Legends install:

| | |
|---|---|
| Distinct mobs **killed** | 580, across 42 zones, 6,280 kills |
| …with a level from /consider | 434 (**75%**) |
| Every NPC-shaped name the log **mentions at all** | 1,061 |
| Rares the client's Hunter achievements name (with a zone) | 766, of which **67 met (8.7%)** |
| A log-derived bestiary, therefore | ~1,279 rows |
| What EQLBase lists for the same game | **5,349 names** (9,026 listings with level variants) |
| Coverage | **~24%**, and of our rows only 48% were names the reference even lists |

Per row we could offer a name, a zone, a level three times in four, and a
health estimate for the ones we killed. Faction was derivable from the log
too (388 mobs, 70 factions, by attributing the faction lines that follow a
kill), and what a mob casts (300 casters), and what we saw it drop (249
mobs). Nothing else exists: the client ships no NPC table, no spawn data, no
loot tables — see [eq-client-files.md](../domain/eq-client-files.md).

So the honest description of the log-derived version was "a record of the
mobs *you* have met", which the Mobs tab (F25) mostly already was. It would
not have been a bestiary, and it would have lost to a site one click away —
a site this app already links to.

The owner asked for other sources to be researched. Of the five community
sites, one is decisive: **EQLBase publishes its data as static JSON**
(`/data/search-index.json`, and `/data/npcs/<id÷1000>.json` shards), its
`robots.txt` is `Allow: /` with no exclusions, the files carry
`Access-Control-Allow-Origin: *` and revalidate by ETag, and its data is
Legends-specific rather than classic-EverQuest guesses. It states **no
licence**. Gnoll Guard's `robots.txt` forbids automated access outright and
is therefore link-out only. The P99 wiki and the emulator databases describe
a different, twenty-year-old game, and their numbers would be confidently
wrong here.

## Decision 1: fetch at runtime, cache locally, attribute on screen — do not bundle

The app fetches EQLBase's published files on the user's behalf, when the user
asks, and caches them under `%AppData%\EQDeeps\reference\` (`--referenceRoot`).
It does not redistribute them. Absence of a licence is absence of
permission: bundling a copy in the installer would be taking something nobody
granted, and their own position is that the data "remain[s] the property of
their respective owners". Fetching is different in kind — it is the same
request the user's browser would make, for a file the site invites anyone to
take, and the bytes stay on their machine.

Every screen showing this data names the source and links to the page it came
from, and `NOTICE` records it. If the site's author later grants permission,
bundling becomes a one-line change to where the index comes from; the ask is
worth making, and this ADR should be amended when it is answered.

## Decision 2: nothing leaves the machine until someone asks, and a switch turns it off

The README promises no cloud and no telemetry. This is the first feature to
reach a third party for *data* (auto-update already reaches GitHub, ADR-010),
so it is built to keep that promise legible:

- **No background refresh, no fetch at start-up.** The store fetches on the
  first search or stat block anyone asks for. Never open the Bestiary and the
  app never speaks to anybody.
- **What goes out is a GET for a static file whose name is a number** — no
  query string, no cookie, no body, nothing about the player, their character,
  their log, or what they were fighting. The user agent identifies EQDeeps,
  because a site owner deserves to know who is calling.
- **The index is revalidated at most once a day**, with an ETag, so the usual
  cost of a session is a 304 and no transfer.
- **Settings → "Look mobs up online"** turns it off entirely, and
  `--no-reference` does the same for anyone who wants it off before the UI
  loads. Off means off: the store returns nothing and fetches nothing.

## Decision 3: never load-bearing, and tested without a network

Nothing the parser, the fights, the queries or any measured number does
depends on this. A failed fetch, an offline machine, a corrupt cache or a
changed shape leaves the app exactly as it was, reports itself in
`ReferenceStatus.Error`, and is never thrown. An index that parses to nothing
does not replace a good cached one — their site is in stated early alpha and
this app must not break when it moves.

`IReferenceSource` is an interface and every test uses a fake. CI must not
depend on somebody's website being up, and a feature that phones a third
party is exactly the one that must be provable offline.

## Decision 4: their listing, our measurement, side by side and labelled

The Bestiary shows both and never blends them. A reference site lists a
mob's health; F25 *measured* what it took to kill one, on this server, at
this instance difficulty. Those are different claims and the difference is
information: on the owner's data a bok ghoul knight reads ×0.93 of the listed
health in the open world and ×2.09 at the Fused tier — which is F25's whole
thesis, visible in one row, and tells you the listed number is the open-world
baseline.

Matching a log name to a listing needs care, because the same name is listed
at several levels. The /consider levels the session has seen are the join key
(`NpcIndex.Resolve`), and the result says whether a level corroborated it:
by name alone, listed health landed within a sane distance of measured
damage-to-kill 55% of the time across the owner's 60 most-killed mobs; joined
on a consed level, 60%, with the median ratio moving from ×1.12 to ×1.08 —
about what overkill alone should cost. So the app shows the match, says what
backed it, and leaves the reader to judge.

## What was considered and not done

- **A log-derived NPC registry** (the original plan). Measured at 24% coverage
  with mostly empty rows; the Mobs tab already answers "what have I fought".
  Two drafted files were binned rather than shipped.
- **Bundling the dataset** — 11 MB, trivial to ship, and not ours to ship.
- **The Hunter achievements** as a rare checklist: 766 names, 699 of them
  never met, offering a name and a zone and nothing else. A real feature, but
  a different one ("what is left to hunt in this zone"), and better answered
  per-zone than as rows in a bestiary.
- **Gnoll Guard**, whose `robots.txt` disallows automated access — link-out
  only, as ADR-019 already has it.
- **Project 1999 and the emulator databases**: a different game's numbers.

## Consequences

- New store, new flag (`--referenceRoot`), new kill switch (`--no-reference`),
  and `%AppData%\EQDeeps\reference\` joins the recomputable caches.
- The Bestiary is the first view whose content is partly not ours. It says so,
  every time, in the panel it appears in.
- If EQLBase changes shape or disappears, the Bestiary empties and says why;
  nothing else in the app notices.
- Open: item icons could come from the same source (`iconId` is already in the
  data), and the same index could seed F21's level-normalized DPS — both are
  cross-checks against our own measurements rather than replacements for them.
