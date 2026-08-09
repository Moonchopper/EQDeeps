# ADR-013: Incoming damage — the feed and the learned attack profile

Status: accepted (2026-08-08). Scope: feature F26.

## Context

The app answers "what did I do to it" in a dozen ways and "what did it do to me"
in one: the Tanking source, aggregated over the current time frame. Two things
are missing from that, and they are missing in opposite directions.

**Underneath it**, the sequence. A player who wants to know what killed them
wants the last twenty seconds in order — three parries, then a 900-point crush,
then nothing. Every table in this app is an aggregation, and aggregation is
precisely the operation that destroys ordering.

**Above it**, the memory. The Tanking view resets when the log closes. "How hard
does a dar ghoul knight in Old Guk tier 3 hit" is a question with a stable
answer that the app re-derives from scratch every session and then forgets.

F25 solved the mirror of the second problem for mob health. This ADR is about
why the same shape does *not* transfer unchanged.

## Decision 1: the key carries the defender's level

Mob health is a property of the world. Damage-to-death is the same number
whoever deals it, which is what makes F25's per-server pooling honest: every
character on the account is measuring the same thing.

How hard a mob hits is not that. It is a fact about a **pairing** — the mob's
offense against a particular defender's mitigation, level, and defensive skills.
Pooling a level-40 character's incoming damage with a level-60's produces an
average describing neither of them, and worse, it produces it silently.

So the key is **(mob, zone, difficulty, defender level)**.

Levels are kept **exact rather than banded**. EQ mitigation moves per level, and
blurring five levels together is the error the axis exists to prevent. The real
log bears this out: `An imp protector` in Nagafen's Lair measures 46 against a
level-44 defender and 50 against a level-45 one — small, but real, and pooling
them would have been an unforced choice to not know.

The cost is fragmentation, which is accepted. A character sits at one level for
a long time, so the evidence concentrates where it is actually used.

### Where the level comes from, and what happens when it does not

The log is generous about the owner and nearly silent about everyone else:

- **Dings** ("Welcome to level 42!") fix the owner's level from that moment.
  Never read backwards — the level *began* there.
- **/who lines** carry a level for every non-anonymous player in the zone,
  owner included. A /who *observes* a level rather than announcing a change, so
  the first one read for a name is read backwards over the log before it —
  without which a player who types /who once at nine in the evening has no level
  for the eight hours preceding, which is most of the log.

`DefenderLevels` implements exactly this, mirroring `ContextTimeline`'s rules and
inheriting its one gap: a de-level is never logged, so a /who read backwards
across one reports the level the player ended on.

Anyone the log never levelled — most group members, every pet, every anonymous
player — keys to **null, which is a bucket rather than a guess**. On the real
log that is 357 rows out of 1,385. Folding them into the owner's level would
invent the one thing that was not observed.

Rejected: inferring level from con lines or from damage taken. Both are
derivable and both would key the index on an estimate, which is how you get a
confident wrong answer instead of an honest empty one.

### Amendment (2026-08-09): the level axis is also a loadout axis

Shipped in v0.9.3 and corrected immediately, because a real user's data looked
like it had a six-day hole in it.

On EQ Legends a character carries several **class loadouts and each levels
independently** — the owner's log dings to 41, then to 11 an hour later, then
climbs again, while a `/who` the next day reports 44. Swapping is not logged at
all (see [log format §3.9c](../domain/eq-log-format.md)), the same silence F24
already documents for gear.

The *key* survives this unchanged, and is arguably better for it: a different
loadout is a different class with different mitigation, so its numbers belong in
their own rows, and keying on level puts them there. What did not survive is the
idea of a **single current level**. The panel defaulted to filtering on the most
recent ding; for a three-loadout character that is one loadout out of three, so
254 of 1,424 rows vanished and the list appeared to jump from today to a week
ago. The filter was doing exactly what it was told, over a question that has no
single answer.

Fixed by replacing "my level" with a level picker that shows everything by
default, orders its entries by recency rather than numerically (the loadout you
were just playing is the one you want, not the lowest-numbered one), and states
plainly how many rows any narrowing is hiding.

The general lesson, worth applying past this feature: **a filter that hides rows
must say so.** A silent default filter is indistinguishable from missing data,
and the user cannot debug what the UI will not admit to.

Not fixed, because it is not recoverable: fights between a swap and the next
ding on the new loadout are attributed to the loadout that was put away. A
`/who` corrects it from that point on.

## Decision 2: a rolling tally, not a bag of samples

F25 keeps every kill, on the stated grounds that a quantile cannot be maintained
incrementally without the values. That reasoning is right and does not survive
contact with a tanking log: the owner's log holds 122,167 incoming records
against 3,854 fights. Hits arrive three to four orders of magnitude faster than
kills.

So the persisted form is a **rolling tally** per (key, skill): counts, totals,
extremes, and a **log-spaced histogram** of landed hit sizes — four buckets to
the doubling, so every bucket is ~19% wide and one scale serves a 12-point rat
and a 4,000-point raid boss equally. Quantiles come back off the histogram in
bounded space.

What is given up, relative to F25: the ability to drop the oldest samples and
so *follow* a server that rebalances a zone, and the ability to explain a bad
number after the fact by looking at what went into it. Both are real losses. The
alternative was a file measured in tens of megabytes.

**Idempotency** is by fight start. Re-opening a log replays every fight in it —
the normal case, several times a session — and a cumulative tally double-counted
can never be un-counted, so this is the invariant the design turns on. Each key
remembers the fight starts it has folded in (capped at 500, oldest dropped); a
fight older than the oldest remembered is treated as already counted, because it
cannot be distinguished from one that fell off the end.

Only **closed** fights are read. Harvesting one still in progress would bank its
opening seconds and then reject the finished version as a duplicate — every
fight's first few swings and nothing after them.

## Decision 3: no concurrency filter

F25 discards kills that look like two mobs of one name, because a fight keyed by
name banks both their damage into one total — one sample too high and one too
low, from a single pull.

That failure mode does not exist here. This measures the size and outcome of
**individual swings**, and two identical mobs swinging are drawing from one
distribution. Twice the evidence, same answer. Nothing is discarded.

## Decision 4: the headline is melee, and confidence is graded on evidence

Two corrections that the real log forced, both of which the synthetic fixtures
would never have surfaced.

**The headline hit-size figures are melee only.** A forsaken revenant in the
Plane of Hate lands 209 punches averaging 66, 752 damage-shield ticks averaging
15, and four Shocks of Swords averaging 582. Pooled, that is "average hit 35" —
a number true of none of the three, dominated by the one the mob is not choosing
to do. So avg/median/band/max/min answer "how hard does this thing swing", the
rates answer "how often does it connect", and spells and shields are carried in
the totals and broken out per skill where they read as themselves.

**Confidence is graded on evidence and on knowing the defender, never on
spread.** F25 reads a wide spread as the method failing, correctly: a mob has one
health. A mob's melee has a real range, often four-fold from minimum to maximum,
so grading it that way would mark the most honest rows as the least
trustworthy. Instead: High at 200+ landed swings against a known level, Medium
at 40+, and an **unknown defender level caps the grade at Medium however much
evidence there is** — a thousand hits pooled across unknown levels is a thousand
hits describing nobody. On the real log that grades 95 High, 344 Medium and 946
Low out of 1,385.

The band is still reported everywhere the median is, for the opposite reason to
F25's: not because the number might be wrong, but because the spread is itself
the answer.

## Decision 5: the raw feed is not a QuerySpec

Every table, chart and live meter in this app is a `QuerySpec`, and adding a
special-case rendering path is the thing CLAUDE.md tells you to check yourself
on. This is the exception, and the reason is narrow: **the subject of the feed is
the order swings arrived in**, and every viz the query model offers aggregates
that away. There is no grouping of "three parries, then a 900-point crush, then
a death" that is still that sentence.

`POST /api/sessions/{id}/hits` therefore takes a `QueryScope` — the same scope
semantics as `/query` and `/timeline`, so it answers over the app-wide time
frame like everything else — and returns the tail of the stream. The tail is a
ring buffer rather than a collect-then-truncate, because the scope can be an
entire evening and holding every incoming record of one to hand back two hundred
is the kind of thing that only shows up on somebody else's machine.

Avoided swings are in the feed. A feed that only showed the hits would answer
"what killed me" with the half of the story that has numbers in it.

## Decision 6: both learned indexes are ordered by recency

Mob health originally listed best-known first — High confidence, then most
kills — and the attack profiles copied it. That is the wrong key for a list
whose contents accumulate for months. The rows a player wants are the ones about
the camp they are standing in, and ranking by evidence buries tonight's mob
under every zone the account has ever worked; worse, it buries it *further the
longer the app is used*, so the feature degrades exactly as it succeeds.

Both indexes therefore sort by last-fought, newest first, with evidence as the
tiebreak. Confidence is a column — it does not also need to be the order. The
Mobs tab is changed to match rather than left inconsistent with the tab beside
it.

This makes the sort key something the reader has to be able to see, so the
attack table gained a **Last fought** column and both tables now render the
instant date-aware (`fmtWhen`): a bare clock time is not a date, and "22:33"
above "09:14" reads as broken until you know they are different days, which the
row never said. Today stays a time, older rows grow the day, and last year's
grow the year — no row carries a component that is the same for every row on
screen.

## Decision 7: what counts as incoming

The **attacker must be definitively an NPC**; the **defender only has to not
be one**. This is exactly what the Tanking source already does, and matching it
was not obvious — the first implementation required a verified player on the
receiving end and silently dropped most of a group, since group members
typically never speak in a player-only channel, join a raid, or turn up in a
/who. The article-and-spaces test still keeps mob-on-mob swings out.

## Consequences

- Two new endpoints (`GET /attacks`, `POST /hits`), a new per-server store under
  `%AppData%\EQDeeps\attacks` with a `--attackRoot` test redirect, and a new
  Overview sub-tab. The Tanking standard view is unchanged and still answers the
  aggregate half of the question.
- Profiles are swept on the same 1 Hz expiry tick as kills, for the same reason
  (a fight is only final once the timeouts have had their say) — but they never
  mark the fight list dirty, because nothing in it reads them.
- `DefenderLevels` walks the whole record stream, so it is cached against the
  record version in `SessionHost` alongside `ContextTimeline`. Uncached, the
  once-a-second sweep would be the most expensive thing the server does on a
  multi-gigabyte log.
- **Riposted swings are invisible**, and cannot be recovered here. A swing the
  defender riposted is written as the defender's counter-attack and the attempt
  records nothing (`DamageParser`, deliberately). Every rate is therefore "of
  the swings the log accounted for", and the UI says so rather than implying a
  denominator it does not have. Fixing it means changing the parser, which would
  move `undefendedRate` and every existing tanking number with it — out of scope
  for this feature, worth its own change.
- Like mob health, this is a **cache**: every swing in it came from a log file
  that still exists, so a corrupt file starts fresh without ceremony and a
  failed write is swallowed. The demo log is excluded — its swings are a
  fixture, and teaching the app about a real server's mobs from one would poison
  evidence.
- F21 (mob-normalized DPS) gains a second axis: mob offense measured per
  defender level, beside F25's mob size.
