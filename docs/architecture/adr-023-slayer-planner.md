# ADR-023: Slayer progress is read from the player's own export, and where to hunt is worked out from the reference — the app never counts a kill toward an achievement itself

Status: accepted (2026-09-20). Scope: F35 — the achievements-export grammar
(`EQDeeps.Core.Achievements`), the creature-type table (`slayer-races.tsv`),
the zone atlas built over the F30 reference shards, the `city` column of
`zones.tsv`, the `/api/sessions/{id}/slayer*` endpoints (the export is one
character's, so it hangs off the session as the inventory dump does) and the
Slayer view. Builds on
[ADR-020](adr-020-npc-reference.md) and inherits every promise it makes.
(021 and 022 are taken by the companion-features design, PR #98, which had
not merged when this was written.)

## Context

The owner's ask (2026-09-20): track the Slayer achievements, and help plot a
way through the rest of them — above all, *say which zone to hunt a creature
type in*, without wrecking faction, and never by sending anyone into a major
city. Today that is an evening of wiki tabs per achievement.

Slayer achievements count kills **by creature type**: 100 kobolds, then
1,000, then 5,000. Three facts shape everything below, and all three were
measured before anything was designed:

| | |
|---|---|
| Kill achievements in the owner's export | **118** (20 complete), over 225 distinct creature-type terms |
| What the log says about a corpse's type | **Nothing.** A slain line names the mob; race is never printed |
| What the game will tell you if asked | Everything: `/outputfile achievements` writes every achievement, component and `have/need` count to a file in the install ([grammar](../domain/eq-client-files.md)) |
| Reference listings carrying `race`, `respawn`, `spawnChance`, per-zone `spawnPoints`, `factionHits` | **All of them** (2,997 of 2,997 in the 26 shards already cached on the owner's machine) |
| Reference *index* rows carrying race | **None** — name, kind and id only |
| Shards the index implies / projected size of all of them | **79 / ~10.9 MB** |
| Terms joined to a reference race by spelling alone, from a third of the world | 77 of 225 |
| Factions the export itself says this player is working on (`Get maximum faction with …`) | **40**, 9 already maxed |
| Listings whose faction hits are a placeholder, not data | **299 of 2,997** — see Decision 5 |

A throwaway spike over those 26 cached shards ranked zones the way a player
would: bears → West Karana (69 spawn points, nothing lost), kobolds →
Nagafen's Lair (114), wolves → Kithicor, and barbarians → *nowhere good*,
every candidate costing Wolves of the North — which is the honest answer and
exactly the evening of wiki tabs, done in a second.

(With all 79 shards, as built: **bears lead with Nektulos Forest** — 177
spawn points of black bears and young kodiaks, up to ~996 an hour — and
West Karana is second; the spike simply had not read Nektulos. Barbarians
came out less bleak than the spike said, too: twelve clean zones exist, all
thin — West Commonlands' 27 an hour is the best of them — and every *rich*
one (Lake Rathetear at 150, West Karana at 118) costs a faction the owner is
building. A later reader should not take the spike's order for the expected
one.)

## Decision 1: the export is the count — the app never keeps its own

Progress shown is what the export says, stamped with the export's age, and
nothing else. The game counts by race id; no log line carries one; any tally
the app kept itself would be a guess wearing the game's number. So the
tracker reads `<Char>_<server>-Achievements.txt` from the install the log
lives in — the same way F29 reads the inventory dump — re-reads it when it
changes, and tells the player the command that refreshes it.

A log-derived *estimate* of what has happened since the export ("+37 kobolds
by the log") is a later slice, and when it comes it is shown beside the
game's number and never added into it — the rule ADR-020 Decision 5 set for
listed against measured.

## Decision 2: what counts as a "Slayer" achievement is the file's own category

Anything under a `Slayer: …` category. A **kill component** is a counted
component (`have/need`) or a completed one in `Slayer: Conquest`, `Special`
or `Skill`; `Slayer: General` holds meta-achievements, whose components point
at others by title. The grammar doc records why a title is neither a key nor
a reliable pointer (93 of 179 references match verbatim, 59 match nothing),
so: identity is category + title + position; a reference keeps the `C`/`I`
it was written with; resolving it is for linking only; and an unresolved
reference is still a row — most are creatures this game does not have yet.

Nothing here is Slayer-specific below the view. The parser reads the whole
file (494 achievements), because Hunter, Exploration and the unlock
achievements are the obvious next readers and Decision 5 already needs one
of them.

## Decision 3: creature-type words are joined to races by a table of our own

The export says `Sporalis`; the reference says `Fungusman`. `Animated Hands`
/ `Reanimated Hand`, `Wisps` / `Will-O-Wisp`, `Fay Drakes` / `Fae Drake`,
`Lizard Men`. Neither side states a race id, so there is no key to join on
and the join is a **hand-authored table**, `slayer-races.tsv`: term → the
reference race names it counts. It is seeded mechanically (singularise,
match the reference's vocabulary), corrected by hand, and is ours — MIT,
data we wrote, no licence question.

The client's own plural table (`dbstr` 12) was the tempting alternative and
is not used at runtime: it knows 182 of the 225 terms, would make the join
depend on reading the install, and still would not say that a Sporali is a
Fungusman. It is a fine authoring aid and nothing more.

Written against the whole reference (2026-09-20, all 79 shards): it has
**111 race labels**, and the table has 109 rows — 87 where the term is simply
a label's plural and 22 written by hand; the other hundred-odd terms have no
location in this game. Three
things the authoring turned up, all recorded in the table's own header:

- **A lead-in belongs to every term after it.** `Clockwork: Beetles, Boars,
  Dragons…` is clockwork beetles, not beetles. Strip the lead-in and the
  planner sends someone to kill ordinary beetles for an achievement they
  will never move. So terms carry it (`Clockwork Beetles`), and only
  `Clockwork Gnomeworks` has anywhere to go.
- **The Conquest tier names loosely what the Skill tier names exactly** —
  `Cubes` / `Gelatinous Cubes`, `Eyes` / `Evil Eyes`, `Apes` / `Gorillas`.
- **The labels are the site's, and some are wrong.** Every panda is filed
  under `Ulthork`, werebats under `Kobold`, mummies under `Zombie`,
  nightmares under `Unicorn`. The atlas is grouped by what the file says, so
  the table maps to the label, mistake included — and the view names the
  mobs, so a player sees "a panda cub" and not a race they have never heard
  of.

**The table is a claim, so it is shown as one.** Each achievement says which
races the app took its words to mean. A term that joins to nothing says "no
known location" rather than vanishing — for most of them (Shissar, Vah Shir,
Drakkin) that is simply true of a game that has not opened those zones. And
the owner's own numbers check it: a term with `have > 0` proves *some* race
in this game satisfies it (30 Sporalis were killed somewhere), which is how
the aliases above were found and how the rest should be.

## Decision 4: a zone's worth is its respawn supply, shown as the facts it came from

For a set of races R and a zone z:

```
supply(z, R) = Σ over listings in z whose race ∈ R
                 of spawnPoints × spawnChance × 3600 / respawn        kills/hour
```

— what the zone yields if everything in R were killed the moment it stood
up. It is an **upper bound and a ranking signal**, never a promise: it knows
nothing of how fast this player kills, how far apart the spawns are, or who
else is there. So a row never shows the score alone. It shows what made it —
spawn points, respawn, level span, how many distinct names — and the score
only orders the rows.

Level is the player's control, not the app's guess: a "mobs up to level N"
cap, defaulting to the character's level where the session knows it. Legends
gives one character three levels ([loadouts](../domain/eq-legends-loadouts.md)),
and which one they are hunting on tonight is theirs to say. **There is no
lower bound**: a trivial kill counts toward a Slayer achievement on Legends
(the owner, 2026-09-20), so the thickest spawn the player can reach is the
best one however grey it cons, and a starter zone is a perfectly good answer
for a level-50 character short of a hundred snakes.

## Decision 5: faction is a cost on the zone, measured against the factions the export names

**Which factions matter** is the hard part of "don't wreck my faction", and
the export answers it: its unlock achievements (`Untapped Potential: Races /
Classes / Deity`) are lists of `Get maximum faction with X.` — 40 factions on
the owner's file. Those are the **protected** set: the one place the game
itself says which standings this player is building. No settings screen, no
asking the player to enumerate factions they may not know the names of.

**What a kill costs** is the listing's `factionHits`. A zone's cost for R is
the supply-weighted hit per kill on each protected faction. A zone that loses
any protected faction is **listed but not recommended**, with the loss
written on it ("Wolves of the North −10 a kill"); one that *raises* a
protected faction says so, because for someone working on Kelethin's
factions, Crushbone is two jobs at once.

**A listing with no primary faction has no faction hits.** 299 of 2,997
cached listings have `faction: null`, and every one of them — a level-1
sewer rat, a moss snake, a level-65 named — carries the identical triple
`Deepwater Knights −1000 / Gate Callers +1000 / Heretics −1000`. No listing
*with* a primary faction carries that signature by accident (144 mention
Gate Callers, all with real hit lists). It is the site's placeholder row, it
is the single most alarming number in the data, and read literally it would
have struck every snake in Kithicor off the list. The rule is the null
check, not the signature, so it survives them changing the placeholder.

**Where a loss would leave the player comes from a second export**
(amended 2026-09-20, once the owner had written one). `/outputfile faction`
writes every standing the character has ([grammar](../domain/eq-client-files.md)),
and the two files turned out to answer *different* questions, which is why
the planner reads both:

| | the achievements export | the faction export |
|---|---|---|
| says | whether a loss can still cost an unlock | where a loss would leave the standing |
| Kazon Stormhammer | `C` — complete | **0** |
| Knights of Truth | `C` — complete | **1,919** of 2,000 |
| Storm Guard | `C` — complete | **620** |

A completed `Get maximum faction with X.` does **not** mean X is at maximum:
the owner's Dwarf unlock auto-completed at character creation with Kaladim's
factions at zero, and a component earned by standing stays earned after the
standing falls. Six of the forty protected factions disagree this way on the
owner's files. So each faction effect carries both facts — whether its unlock
is already earned, and the standing now beside the standing **projected**
after the kills the achievement still needs (`standing + hit × remaining`,
clamped to the file's ±2,000). "−10 a kill" is abstract; "you are at 620 and
the hundred barbarians you need would leave you at −380" is the decision.

The rule stays the simple one the owner agreed to — a zone that loses *any*
protected faction is listed, never recommended — and the unlock's state is
shown, not used to soften it. If that proves too strict for someone whose
unlocks are all earned, relaxing it is a one-line change with the evidence
already on screen.

The three sources spell factions three ways (`Coalition of Tradesfolk` /
`Coalition of Tradefolk` / `Coalition of TradeFolk III`; `Da Bashers` /
`DaBashers`; `Freeport Militia` / `The Freeport Militia`), so they are joined
on a key — case, apostrophes, backticks, whitespace and a leading "The"
ignored — plus two aliases no rule would find. Measured: 36 of the 40
protected names reach the faction file on the key alone, 40 of 40 with the
aliases; 127 of the reference's 142 hit factions reach it, and the rest are
the reference's own bookkeeping (`KOS_animal`, `Beta Neutral`).

The faction export is **per class loadout** — the file is
`<Char>_<server>-<CLASS>-Factions.txt` — so the app reads the most recently
written one and says which class it was. Still not solved: the log's own
faction lines after a kill, which are the measured counterpart to these
listed hits and belong beside them one day.

## Decision 6: a city is never recommended, and the zone table says which zones are cities

`zones.tsv` gains a `city` column, hand-authored like the rest of it. A
flagged zone contributes nothing to any supply and appears in no ranking;
the view says how many were left out so the omission is legible. The flag
means *a player city* and not "anywhere with guards". The list, agreed with
the owner on 2026-09-20 — every city among the 79 zones the reference
covers: South and North Qeynos, Surefall Glade, North, East and West
Freeport, Rivervale, Erudin and Erudin Palace, Halas, the three Neriaks,
Oggok, Grobb, Ak'Anon, North and South Kaladim, both Felwithes, and Paineel
(23 rows of the table, Freeport's two spellings included). **Deliberately
not flagged:** Highpass Hold, High Keep, Kerra Isle and the Kelethin half of
Greater Faydark are hunting zones with a town in them — Kerra Isle is where
the Kerran achievements get done at all — and it is Decision 5, not this
one, that keeps a player off their guards. A city that opens with a later
expansion (Cabilis, Shar Vahl, Thurgadin) gets its flag when its zone does.

The measurement says this rule is not decoration: cities are where the
playable races stand thickest, the people-race achievements (*Barbarous*,
*Highly Uncivilized*, fifteen races of *I'm a People Person!*) are most of
what is still open, and an unfiltered ranking put North Qeynos on the list
for snakes.

## Decision 7: the atlas reads every zone's shard, once, because the index cannot answer "where"

The index has no race column, so "where do kobolds live" cannot be asked of
it: every shard has to be read — 79 files, about 11 MB. That is a bulk read
of a third party's site, and ADR-020's promises bind it:

- **Opening the planner is the ask** (ADR-020 Decision 2, as amended for the
  Bestiary). Never open it and nothing is fetched. The tracker half
  (Decisions 1–2) needs no network at all and works with the switch off.
- **One file at a time, with a pause between them**, progress on screen
  ("zone 12 of 79"), and every zone usable the moment it lands. A user
  reading the site's zone pages would pull the same 79 files; the app should
  not pull them faster than a person could.
- **Cached as shards already are**, and revalidated no more than weekly —
  79 conditional GETs a day would be rudeness for data that changes with
  patches.
- `--no-reference` and the Settings switch turn it off, and the view says
  that is why it has no zones to suggest.

The atlas itself — race → zone → supply and cost — is **derived, never
stored**, like `ZoneLevels`: it costs one pass over files already on disk
and cannot go stale against them.

**As built (2026-09-21), and two things building it turned up:**

- *A cached shard used to be kept forever.* Only the index revalidated; a
  zone fetched in August was August's data until the cache was deleted. The
  weekly rule above is therefore new behaviour for every reader of a shard,
  the Bestiary included: younger than seven days, no request at all; older,
  one conditional GET with the stored ETag; a `304` keeps the file and
  freshens it; **any failure keeps the cached copy**, because ADR-020
  Decision 3 says this data is never load-bearing and a site having a bad
  day must not empty a view that worked yesterday.
- *The "Look mobs up online" switch is enforced in the UI and nowhere
  else.* It lives in `ui-settings.json` and never reaches the server, so a
  new view that simply called the atlas endpoint would have read the site
  with the switch off. **Every view that reads the reference must honour
  `useReferenceEnabled()` itself**; the hunting panel does, and its check is
  two-sided — zero reference requests with the switch off, exactly one
  `POST /api/reference/atlas/start` with it on — because "off is silent"
  alone cannot tell a correct panel from one that never fetches.
  `--no-reference` remains the server-side switch and is honoured there.
- The walk runs once per run of the app, from that one POST and nothing
  else. With every shard already on disk and fresh it completes in about two
  seconds and sends nothing.

The owner's decision (2026-09-20): **"yes, let's try it out for now"** — so
it ships, and "for now" is the operative phrase. The bulk read is still the
one thing here worth the site author's blessing, it joins the ask ADR-021
already owes them for items, and if the answer is no this decision is the
one that changes.

## Decision 8: the plan is greedy, explainable, and says what it ignores

"Semi-optimal" is the owner's word and the right ambition. The structure
that matters is **overlap**: one kobold is a kill toward three achievements
at once, and one zone can feed six creature types whose respawns run in
parallel. So the plan is a greedy walk, re-scored after every stop:

1. Every open kill component c has `remaining(c)` and a race set. A kill of
   race ρ is worth `Σ 1/remaining(c)` over the open components that count ρ
   — a kill matters most to the achievement it nearly finishes, which brings
   completions forward instead of spreading effort thin.
2. A zone's score is `Σ supply(z, ρ) × worth(ρ)` over the races it holds —
   cities out, level cap applied, zones that cost a protected faction out.
3. Take the best zone; stay until the first component it feeds would
   complete at supply rate; subtract what that stay yields from *every*
   component it feeds; re-score; repeat. Adjacent stops in the same zone
   merge.
4. Show the next ten stops, not the whole road. A 5,000-kill Conquest makes
   the full list a fantasy; the near end of it is what gets used.

Each stop says why it is there — what it finishes, what else it feeds, what
it costs — so the player can overrule it, which a better optimiser with an
opaque answer would not allow. Tests pin the walk against a hand-computed toy
world (three zones, three races), per the house rule for anything with a
formula in it.

What it deliberately ignores, and says so on screen: travel (a stop is hours
long; the World graph can order stops later), how fast this player actually
kills, named camps and placeholders, groups, and instance difficulty tiers.

## Decision 9: a view of its own, not a dashboard

The Bestiary is the precedent: achievements and reference listings are not
records in the record store, so there is no `QuerySpec` to write and the
"should this be a query?" test (CLAUDE.md §1) answers no. A Slayer view sits
in the rail beside the Bestiary and the World. The one part that *is* a
query is the later since-export estimate — kills grouped by mob, joined to a
race — and it should be built as one when it comes.

Every zone named in the view opens the Map; every mob opens the Bestiary.
Those doors already exist and the owner's standing rule is that an entity is
a door everywhere it is named. As built they are the same App-owned
callbacks the Bestiary and the Map hand each other, **without a crumb back**:
the trail's `Crumb` type is closed over those two views, and an inert or
mislabelled chip is worse than none. The app's own back arrow returns to the
Slayer view. Widening `Crumb` is a small change that belongs with slice 3,
when plan stops become doors too.

## Slices

1. **Tracker.** The export grammar, the endpoint, the Slayer view: every
   kill achievement with its progress, nearest-to-done first, the export's
   age and the command that refreshes it. No network, no reference data.
2. **Where to hunt.** `slayer-races.tsv`, the `city` column, the atlas, the
   faction rule **and the player's real standings** (`/outputfile faction` —
   it was to be a slice of its own, but the owner wrote a sample the same
   day and a faction cost without a standing beside it is half an answer):
   pick an achievement, see its zones ranked with the facts and the costs.
3. **The plan.** Decision 8, across everything open; stops open the Map.
4. **Since the export** — the log's kills joined to races, shown beside the
   game's count; doubles as the check on `slayer-races.tsv`.

## What was considered and not done

- **Counting kills from the log** as the tracker's number. No race in the
  log; joining by name inherits every ambiguity ADR-020 Decision 5 measured
  (a name listed at several levels, sometimes as several races); and the
  game's own count is one command away.
- **Reading the client's achievement tables** for definitions. They carry
  titles and component text but no required counts and no progress, and the
  export repeats everything they do say.
- **A settings screen for protected factions.** The export names them. If
  someone wants to stop caring about one, that is a later toggle, not a
  prerequisite.
- **A true optimiser** (set cover / ILP over zones × time). The inputs are
  upper bounds from a community site; false precision on top of them would
  be worse than a greedy walk whose every step can be read.
- **Bundling a race → zone table.** It is the reference's data, and
  ADR-020 Decision 1 already settled that it is fetched, never shipped.

## Consequences

- A second player-written file is read from the install, and the parser is
  general: Hunter and Exploration achievements can be read by whoever wants
  them next.
- `zones.tsv` grows a hand-authored column, and the derive script must carry
  it through a regeneration untouched.
- The reference layer learns to read everything, politely — today its
  fetches are serialised behind one gate but nothing paces them, because
  nothing ever asked for more than one. `NpcDetail` grows the two fields the
  atlas needs and the shard parser has been dropping (`factionHits`,
  `spawnChance`); that is a Core change, so every log cache re-parses once,
  as ADR-018 intends. If the site objects, Decision 7 is the only one that
  has to change, and slice 1 is unaffected.
- The two in-game facts this was written on as assumptions were settled by
  the owner the same day: trivial kills count (Decision 4), and
  `/outputfile faction` writes what Decision 5 now describes.
- A third player-written file is read from the install, and it is per class
  loadout — the first input in the app that is.
