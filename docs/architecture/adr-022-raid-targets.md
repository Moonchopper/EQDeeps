# ADR-022: Raid targets are a roster laid over the death records, and zone becomes something a query can group by

Status: proposed (2026-09-20). Scope: feature F31 — `InstanceZone`, two new
query dimensions, one new metric, a hand-authored roster, one endpoint, one
rail view under World. First of the four features in
[ADR-021](adr-021-companion-features.md), which says why this one is first
and what may not be borrowed to build it.

## Context

The ask: which raid targets has this character killed, when, and at what
difficulty — with the ones still standing shown beside the ones that fell.

The neighbouring app's version, read as behaviour: thirty-two hand-listed
names in four groups (open world, and the planes of Fear, Hate and Sky); a
kill is any slain line in the log whose victim is one of them, matched on the
whole name; a kill is *credited* when the character gained experience for it;
difficulty comes off the zone line; nothing is stored, because the log says it
all every time it is read. On top sits a weekly loot lockout, per target per
difficulty.

Almost none of that needs building. `DeathEvent`, `ExperienceEvent` and
`ZoneEvent` are parsed; `ContextTimeline` already turns zone lines into spans
of time clipped to presence; `InstanceZone` already splits the difficulty off
a zone name; `QuerySource.Deaths` already groups deaths by victim; the log
cache (ADR-018) already makes "re-read everything" cost a second. What does
not exist is a way to ask a query **where** something happened. `Dimension`
has player, target, spell, damage type, character and stance — no zone, no
difficulty. F25 and F26 both key on them, but each through its own index.

## Decision 1: zone and difficulty become query dimensions

`Dimension.Zone` and `Dimension.Difficulty`, available to every source, read
from the zone spans `ContextTimeline` already builds and split with
`InstanceZone`. `Dimension.Stance` is the precedent and the pattern: a
step function over time, resolved per record with a cursor, and **not wound at
all unless a spec groups or filters by it** (`UsesStances`), so the queries
that never mention a zone pay nothing.

- **Zone** is the place: `InstanceZone.BaseName`. "The Estate of Unrest" is
  one row whether it was entered at tier 0 or tier 4.
- **Difficulty** is everything after the place, as the log printed it —
  `1 (Awakened)`, `Solo`, `Group 3 (Fused)` — carried through verbatim, the
  rule `InstanceZone` and stances already follow, so a tier the server adds
  shows up as itself. No suffix is `Open world`. The mode is in the label
  because it is part of the difficulty (Decision 2): a solo kill and a group
  kill at the same tier are two rows, not one.
- A record in a load screen, or before the log's first zone line, belongs to
  no zone. It keys to `(unknown)`, exactly as a record outside every stance
  keys to `StanceTimeline.Unknown`. It is never guessed from the zone either
  side.

This is deliberately bigger than raid targets need. A bespoke "raid kills"
endpoint would have been a day quicker and is the special-case path CLAUDE.md
§1 warns about: once zone is a dimension, *damage by zone*, *experience by
zone* and *loot by zone* are panels anyone can build, and the roster is one
more query.

## Decision 2: the zone line has a part we were not reading

Found while checking this design against the owner's log (198 MB, 798 zone
entries). Five zones — Nagafen's Lair, the Permafrost Caverns, the Plane of
Fear, the Plane of Hate, the Ruins of Old Paineel — are sometimes entered
with a marker between the name and the tier:

```
You have entered The Plane of Fear - Group 3 (Fused).
You have entered The Permafrost Caverns - Solo 1 (Awakened).
You have entered Nagafen's Lair - Solo.
```

Thirty-two such entries, no other zone in the log carries one, and every one
of the five holds a raid target. The same five are *also* entered bare and
tiered-without-a-marker, across the same weeks, so it is not a patch that
changed the format. The log could not say what the marker means; **the owner
settled it from play (2026-09-20)**: `Solo` is an instance entered alone, its
difficulty adjusted for one player; `Group` is one entered at group
difficulty, into which others can be invited. It is the solo-versus-group
setting the log-format doc had down as never logged. Why only these five
zones print it is still not known.

What is established is what the parser does with it today: `InstanceZone`
reads "The Plane of Fear - Group 3 (Fused)" as a place called "The Plane of
Fear - Group" at tier 3. So the map for a raid instance resolves to nothing
(the known gap in [eq-map-format.md](../domain/eq-map-format.md) §5.1 is this),
and a zone dimension would show Fear as three places.

`InstanceZone` gains a third part, `Mode` — `Solo`, `Group`, or null — carried
verbatim like the tier word. `BaseName` becomes the place alone.

**F25 and F26 must not change what they measure.** Mob health and attack
profiles are keyed on zone and difficulty, and today a marked instance is
keyed apart from the unmarked one by the accident of its name. That
separation is right, and now for a stated reason: the mode rescales the
instance, so a solo-scaled boss and a group-scaled one are different fights
in exactly the way two tiers are. **The mode stays part of those keys.** The slice that
changes `InstanceZone` has to say, with evidence, what happens to samples
already learned under the old names: they are not to be counted twice and not
to be silently stranded.

Two corrections to [eq-log-format.md](../domain/eq-log-format.md) §3.9b ride
with this, made in the same change that records the finding: solo-versus-group
*is* logged, for these instances; and a marked entry with no tier is a tier-0
**instance**, which the doc said could not be told from the open world.

## Decision 3: a kill is credited when experience arrived for it

A slain line in your log means you saw it die, not that you were part of it.
Credit is the experience line, and on this game it comes **first, in the same
second**:

```
[12:57:45] You gain experience! (1.341%)
[12:57:45] You have slain Lord Nagafen!
```

Checked on the owner's log for three different targets, solo and party forms
alike. The rule: walking the records in order, an `ExperienceEvent` is
*pending* until the next `DeathEvent` within two seconds claims it, and one
experience line credits one death — so two mobs dying in the same second each
take their own. This is a **query-time reading, never stored**, like every
other validity decision in this app: a new metric on the deaths source,
`credited`, beside the `deaths` count that exists.

Known blind spot, stated rather than hidden: a character who gains no
experience (dead at the moment of the kill) is never credited. The roster
therefore shows both numbers — seen and credited — and nothing in F31 rests
on credit alone. (A lockout would; see Decision 6.)

## Decision 4: the roster is data we write, matched on the whole name

`src/EQDeeps.Core/Raids/raid-targets.tsv`, embedded the way `zones.tsv` is:
`name`, `aliases`, `zone`, `group`. Hand-authored from the game, this server's
own kill records and the reference index — not from the companion's list
(ADR-021 Decision 1). **The owner reviews it before it ships**; it is a short
list about content the owner plays, and the only authority on whether
something counts as a raid target on this server.

- Matching is on the **whole name**, under the article-stripped key
  `NpcIndex` already uses (ADR-020). A loose match is a real trap: "Innoruuk"
  appears in 54 slain lines of the owner's log, and almost all of them are
  `Cleric of Innoruuk`.
- `aliases` exists because the game names one target two ways ("Innoruuk,
  the Prince of Hate").
- The roster's `zone` is where the target *lives*, for grouping and for the
  door to the map. **Where a kill happened comes from the record**, not the
  roster — it is the zone dimension of that death.
- Name-only matching, like the companion. If a trash mob ever shares a
  target's name, the roster's zone becomes a filter; nothing suggests one does.

## Decision 5: nothing is stored, and the view is a query plus a list

`GET /api/raids/targets` serves the roster. The view runs one deaths query —
grouped by victim, zone and difficulty, filtered to the roster's names with
the `Values` filter the spec already has — and lays the roster over the rows,
so a target with no rows is drawn as not yet defeated. That is the shape the
Bestiary already has: someone's list, our measurements, joined on screen.

A row needs to say *when*: first and last kill. `QueryRow.Metrics` is numbers
only, so the deaths source gains `firstAt` and `lastAt`. How an instant is
best carried in that bag is the implementer's to propose and the PM's to
accept; it must round-trip to the second, which is the log's resolution.

Per target the view shows the name, where it lives, kills seen and credited,
first and last kill, and a ladder of difficulty tiers with the defeated ones
lit. Every name carries the lookup door (F29) and opens its Bestiary page
(F30), per the owner's rule that an affordance goes wherever the name is. The
view lives in the rail's **World** group. It reads the whole log by default,
because "have I ever killed this" is a lifetime question; the app-wide time
frame narrows it like any other view.

No portraits (ADR-021 Decision 4). No sound, no celebration (triggers and
audio remain out of scope).

## Decision 6: the weekly lockout is left out

The companion models a loot lockout per target per difficulty, resetting
weekly — and marks its own reset moment "VERIFY IN GAME". A lockout view that
is wrong by a day tells someone they can loot when they cannot, which is
worse than no lockout view, and building one needs the reset day and hour
and whether solo and group share a lockout, none of which the log says.

**Owner's call (2026-09-20): leave it alone, and come back to it if its
absence turns out to be a problem.** F31 ships without it. Nothing is lost by
waiting: everything a lockout needs from the engine — credited kills, by
difficulty, inside a time window — is delivered by Decisions 1 and 3, and the
app-wide time frame already answers "what have I killed since Tuesday" for
anyone who types the window. If it is picked up, it is a date calculation and
a toggle, plus those facts confirmed in game.

## Slices

1. **The zone line's third part.** `InstanceZone.Mode`, fixtures from the
   forms above, F25/F26 keys unchanged in meaning, the map lookup fixed as a
   consequence, and the account of already-learned samples. Core.
2. **Zone and difficulty as dimensions; `credited`, `firstAt`, `lastAt`.**
   Tests against **hand-computed** values, per CLAUDE.md §8: a synthetic log
   with kills across two zones, two tiers and a load-screen gap, including
   two deaths in one second and a death with no experience line. Core.
3. **The roster, the endpoint, the view.** Roster reviewed by the owner.
   Core + Server + UI; `api.ts` changes with the DTO in the same commit.

There is no fourth slice; the lockout (Decision 6) is deferred, not planned.

Slices 1 and 2 change Core, so every log cache re-parses once on the next
open (ADR-018) — by design.

## What was considered and not done

- **A dedicated raid-kills index**, F25-style, persisted per server. Nothing
  here is expensive enough to need remembering, and a second copy of what the
  log says is a second thing to keep right.
- **`Mode` as a third dimension of its own.** It rides in the difficulty
  label instead, which is where a reader looks for "how hard was this".
  Splitting it out is one more enum member if anyone ever wants every solo
  run against every group run regardless of tier.
- **Proving a bare zone name was a tier-0 instance** from the
  `… creating instance <Zone> <id>.` lines (128 in the owner's log), as the
  companion does. Worth having; not needed for raid targets, whose instances
  carry the marker. Left for whoever next touches §3.9b.
- **Respawn timers on open-world targets.** A timer; see ADR-021.

## Consequences

- The query model grows by two dimensions and three metrics, and the UI's
  dimension pickers grow with it (`QueryBuilder`, `api.ts`).
- A raid instance's map starts resolving, which closes half of a gap the map
  doc lists as known.
- The roster is the first checked-in data about *content* rather than
  geography. It will go stale when the server adds a raid; a row is one line.
