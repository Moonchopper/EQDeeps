# ADR-011: Gear snapshots

Status: accepted (2026-08-03). Scope: feature F23.

## Context

EQDeeps can say a player's DPS moved. It cannot say why. On EQ Legends the
commonest reason is gear: items carry a `+N` upgrade level that climbs
continuously, and augments are socketed and swapped freely. Attaching that to
the telemetry the app already computes turns "you did 56 sDPS last night and 61
tonight" into something a player can act on.

The blocking question was whether gear is knowable at all from the player's
machine. It nearly isn't. An investigation of the install and the log
(`docs/domain/inventory-file-format.md` §1) found:

- Loadouts on EQ Legends are **class** loadouts, not gear sets. The client
  persists their UI, hotbars and auto-attack skills; the equipment attached to
  them lives server-side.
- A loadout swap emits **no log line**. Neither does equipping anything.
- No client-side file records what is worn — not `eqclient.ini`, not the
  per-character or per-loadout INIs, not `[Bandolier]`.
- The reference implementation has no inventory handling to borrow from.

The single exception is `/outputfile inventory`, which the player must type.

## Decision 1: accept a manual capture, and say so

Gear comes from the player running `/outputfile inventory`; the app watches the
install root for the resulting file and records each distinct version.

The alternative was to build nothing, on the grounds that a feature depending
on a manual step will be used inconsistently and produce partial data. That was
rejected because partial gear data is still strictly more than none, and
because the failure mode is visible rather than silent: a snapshot either
exists for a moment in time or does not, and the UI can say which.

What the decision obliges:

- **The app never issues the command.** It polls for the file (5 s, matching
  the ingestion pipeline's polling style) and otherwise does nothing. Driving
  the game client is not something an analytics tool should do.
- **The nudge explains itself.** "No gear snapshot yet" reads as the app
  failing to find something it should have found on its own, so the panel says
  *why* the manual step exists, quotes the command, and prints the exact path
  being watched — the useful answer when the command appeared to do nothing.
- **Staleness is reported as a fact, not an alarm.** Gear can change at any
  moment without a trace, so the only true statement is how much combat has
  happened since the last proof. The status carries `fightsSince` and the UI
  states it plainly rather than nagging.
- **Re-running the command with unchanged gear costs nothing.** Snapshots are
  identified by a hash over the equipped set including augments — deliberately
  *not* over the whole file, so bank and bag churn never registers as a gear
  change.

## Decision 2: attribution is forward-only

A snapshot applies from its own capture instant until the next one. Time before
the first snapshot has no answer, and the UI says "gear unknown" for it.

The alternative was to backfill — treat a snapshot as also describing the
session that preceded it, so dumping at the end of a night labels that whole
night. It is more useful and it is not true: the player may have changed gear
at any point in the interval. Backfilling would attach gear to fights the
player may not have been wearing it for, which is precisely the error this
feature exists to prevent, and it would do so invisibly.

The consequence is accepted deliberately: the player must dump when they log in
or after changing gear, and if they don't, they get an honest gap rather than a
confident guess. A change is dated at the snapshot that **proved** it, and the
previous snapshot's time travels with it, so the uncertainty window is
available to anything that reports on the change.

## Decision 3: comparison, not correlation

The panel offers damage either side of a gear change, because that is the
question the feature exists to answer. It is two ordinary queries over two time
windows — no query-engine change, no new dimension.

It is labelled as what it is. The two windows differ in mobs, group
composition, and duration as well as in gear, so the panel shows the fight
count on each side and states outright that this is context rather than a
controlled comparison. A gear feature that quietly implied causation would be
worse than no gear feature.

`UpgradeScore` (the sum of `+N` across equipped items and augments) is a
progression marker for ordering and labelling snapshots — explicitly not a
power rating, and nothing should treat it as one.

## Consequences

- **Install-root discovery had to be fixed first** (`LogDiscovery`): it
  hardcoded `Installed Games\EverQuest` and the `DGC-EverQuest` registry key,
  so an "EverQuest Legends" install — on a drive that isn't `%PUBLIC%`'s — was
  invisible unless the game was already running. Publisher directories are now
  enumerated across every fixed drive and uninstall keys matched by prefix in
  both registry views. This also closes the empty "No log open" state with the
  game closed.
- **The watcher resolves the dump from the session's own log path** first: logs
  live in `<install>\Logs`, so the log already being read names the install
  root outright. Discovery is the fallback for logs opened from a copy.
- **Snapshots are the first thing the app persists that it cannot recompute.**
  Parsed records are rebuilt from the log on every open; an inventory dump is
  overwritten by the next one, so a snapshot not kept is gone. `GearStore`
  therefore follows the `RecentLogs` shape (atomic writes, corrupt file starts
  fresh) with a 200-snapshot cap.
- **The demo log gets no watcher.** It describes a character who does not
  exist, so there is nothing to find and nobody to nudge.
- Timestamps are stored as zone-less local time, matching log timestamps. The
  file's `LastWriteTime` is `Local` and would otherwise serialise with a UTC
  offset no other timestamp in the app carries.
