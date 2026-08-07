# ADR-012: Estimated mob health

Status: accepted (2026-08-06). Scope: feature F25.

## Context

"How much health does that thing have" is unanswerable from any data source the
app has. EverQuest never prints a mob's health, the reference implementation
carries no mob-stats table, and community datasets for EQ Legends do not exist —
its instance difficulties are its own invention and nothing outside the game
knows them.

But the log records every point of damage a mob absorbs, and it records when the
mob died. **Damage-to-death is health, plus whatever the killing blow
overshot.** That is a measurement, available from data already parsed, needing
no external table and no server cooperation. This ADR is about turning it into a
number worth showing.

It also unblocks half of F21 (mob-normalized DPS): "is this mob big" is most of
what "was that DPS good for this content" needs.

### What the log says about which mob is which

The owner raised the sharp version of the question: EQ Legends instances vary by
difficulty tier (0–4), by respawning vs non-respawning, and by solo vs
multiplayer — do the logs index any of it?

An investigation of a 66 MB real log found:

- **Difficulty: yes.** The zone-entry line carries it —
  `You have entered The Estate of Unrest 4 (Refined).` — with the number and
  tier word a fixed pair across every zone. See
  [log format §3.9b](../domain/eq-log-format.md).
- **Tier 0: no, and it does not matter.** A tier-0 instance prints the bare zone
  name, identical to the open world. They are the same content, so they share a
  bucket honestly rather than by accident.
- **Respawning vs non-respawning: no.** Not one system message. Every occurrence
  of the word in 800,000 lines is a player in chat.
- **Solo vs multiplayer: no.** Likewise.

Difficulty is not a minor axis. Measured through the production parser, the same
mob's health climbs steadily with it:

```
A froglok ton knight @ The City of Guk       A dar ghoul knight @ The Ruins of Old Guk
   open world   901   ×1.00                     open world  3611   ×1.00
   tier 1      1040   ×1.15                     tier 1      3972   ×1.10
   tier 2      1139   ×1.26                     tier 2      4616   ×1.28
   tier 3      1242   ×1.38                     tier 3      5274   ×1.46
   tier 4      2155   ×2.39
```

The ladder is consistent across mobs and zones (≈×1.15 / ×1.30 / ×1.50 for tiers
1–3, with tier 4 stepping to ≈×2.4), which is strong evidence that difficulty is
the axis, that it is being read correctly, and that keying on it is not
optional — a table that pooled these would be wrong for every row in it.

## Decision 1: the key is (mob name, zone, difficulty), and nothing else

Nothing else is available. The two unlogged settings cannot key anything, which
raises the risk that each bucket is silently mixing two populations.

The data says they are not. Across every key with enough kills to tell, the
damage-to-death distributions are **unimodal with a right tail** — one cluster
plus overkill — rather than the two humps that mixing a solo and a multiplayer
population would produce.

That is evidence, not proof. So the design is arranged to fail loudly rather
than quietly if it is wrong: every estimate reports a p10–p90 band and a
confidence grade beside the number. A mob whose health really does depend on
something unlogged will present as a wide band and Low confidence, which is
exactly what it should say.

Rejected: inferring party size from the damage stream as a proxy for
solo-vs-multiplayer. It is derivable, but it would key the index on a *guess*,
splitting every bucket in two and halving the evidence behind each — paying a
certain cost against an unobserved problem.

## Decision 2: discard kills that look like two mobs of one name

Fights are keyed by NPC name (metrics §1), so two mobs of the same name up at
once are one fight. The first death banks their combined damage; the survivor's
remainder opens a fresh fight that banks far too little. One pull, two wrong
samples, in opposite directions.

They are detected by proximity: **two kills on one key within 20 seconds are
both discarded.** Both sides go, because the inflation and the deflation are the
same event and there is no way to tell from here which sample is which.

The threshold was measured, not chosen. Over the 33 keys with 20+ kills in the
real log:

| rule | median relative IQR | samples kept |
|---|---|---|
| no filter | 0.336 | 100% |
| **kill-to-kill gap < 20 s** | **0.240** | **71%** |
| next fight re-engages < 5 s after death | 0.256 | 50% |
| next fight re-engages < 10 s after death | 0.216 | 40% |

The re-engagement rules target the mechanism more precisely and tighten
marginally further, but discard 50–60% of the evidence. For a feature whose
whole difficulty is having enough kills, that is a bad trade.

Below four surviving samples the filter is **skipped** rather than applied:
with three kills on record, discarding two to be careful leaves nothing to be
careful with. The estimate falls back to every sample and reports Low.

## Decision 3: the median, reported with its spread

The headline is the **median** damage-to-kill, with p10 and p90 beside it.

It is biased high by construction — every sample is health plus overkill — and
that is accepted rather than corrected. Overkill cannot be measured (the log
never says how much health was left), and the number a player actually wants is
"what does it take to drop this", which is the biased one. The alternative, a
low quantile as the headline, trades a known bias for an unknown one: the low
tail is polluted by fights the timeouts split and by merged fights the filter
missed, so it under-reports by an amount nobody can bound.

The band travels with the number everywhere it is shown, in the table and in the
ladder, rather than hiding behind a tooltip. One number would read as a
measurement; this is an estimate with a spread, and it should look like one.

Confidence is graded from the clean sample count and the relative IQR — High at
10+ clean kills within a 25% spread, Medium at 4+ within 50%, Low otherwise. On
the real log that grades 47 keys High, 121 Medium and 328 Low out of 496, the
Low bulk being mobs killed once or twice. A single kill still produces a row:
one kill is a real observation, and "about this much, from one fight" beats
silence as long as it is labelled.

## Decision 4: persist per server, and treat it as a cache

Samples are stored as one JSON per game **server** under `%AppData%\EQDeeps\mobs`
(`--mobRoot` redirects it for tests), capped at 200 kills per key, oldest
dropped first.

Per server rather than per character: a mob's health is a property of the world,
not of who hit it. Every character on an account contributes to and reads from
the same evidence, and — the point of persisting at all — the estimate for a mob
fought last week is already there on tonight's first pull.

Unlike gear snapshots (ADR-011) this **is** recomputable: the samples all came
from logs that still exist. It is a cache, not a system of record, which is why
a corrupt file starts fresh without ceremony and a failed write is swallowed.
The cost of losing it is re-reading logs the user still has.

The cap keeps the file bounded — a camp worked for a week would otherwise
accumulate thousands of identical samples — and dropping the oldest first means
a server that rebalances a zone is followed rather than averaged with its own
past.

The demo log is excluded. Its kills are a fixture, and letting them teach the
app about a real server's mobs would poison an estimate that is meant to be
evidence.

## Decision 5: sweep on the expiry tick, not on death

Kills are harvested by the 1 Hz loop that already expires idle fights, not at
the moment a death line is parsed.

A fight is only final once the timeouts have had their say, and riding that tick
means that has just happened. It also makes replay free: a sweep re-offers kills
the index already holds, the index recognizes them by (key, instant, size), and
re-opening a log — the normal case, several times a session — costs one pass and
no disk write.

The derived lookup the fight list reads is rebuilt only when a sweep actually
banks something new. The fight list is rebuilt on every push, up to 20 times a
second, and re-deriving quantiles over every sample that often would cost far
more than the column is worth.

## Consequences

- Mob health has no UI of its own on the live meter. It is analysis, not a
  status bar: the Mobs tab, a fight-list column, and the tier ladder.
- The fight-list column (`damage dealt ÷ learned health`) is left unclamped
  above 100%. The estimate is a median and the killing blow overshoots, so a
  tough pull really did cost more than the typical one; clamping would hide
  exactly the fights worth looking at.
- Fights now carry the zone they happened in, which nothing else needed before.
  A load screen clears it rather than letting the old zone stick — a fight
  stamped with where the player used to be would land in the wrong instance's
  bucket.
- F21 (mob-normalized DPS) inherits a per-mob size measure and can drop the
  external-dataset option.
