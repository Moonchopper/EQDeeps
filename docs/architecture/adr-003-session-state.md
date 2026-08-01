# ADR-003: Session state — fights, records, identity

Status: accepted (2026-08-01). Scope: phase 3 — the state layer between parsing
and the query engine.

## Design

- **`Session`** owns one log file end-to-end: ingestion → parser → record store
  + fight tracker, all mutated on the single task running `RunAsync`. Character/
  server come from the filename (`LogFileNames`, tolerant of EMU deviations).
  `BatchProcessed` fires per batch on that task — the subscribable point where
  realtime push (phase 5) and a future trigger system attach.
- **`RecordStore`**: append-only, time-ordered list of `(timestamp, GameEvent)`
  with binary-search range lookups and a monotone `Version` for cache keys. It is
  the query engine's source of truth; fights carry only lightweight running
  totals and per-second series for the fight list and live meter.
- **`FightTracker`** implements metrics doc §1 with the reference's constants
  (verified in EQLogParser's `FightManager`/`FightTable`): fights keyed by NPC
  name, closed on death, on 30 s combat inactivity when damage exists, at a 60 s
  hard cap otherwise (which is what lets taunt-opened pulls live), and on zone
  transitions; same name re-pulled is always a new fight; pull-chain grouping
  splits at 120 s gaps; player-cast spells within a 300 s window let
  spell-as-attacker lines join the players' side. All time comes from record
  timestamps, so replay and live tail behave identically (a wall-clock expiry
  tick for the live meter plugs into `ExpireFights` in phase 5).
- **Side resolution**: a fight needs exactly one NPC side. Definitely-NPC =
  article prefix, multi-word shape, or seen dying to players; player-side =
  verified players, mapped/possessive pets, the log owner. An unknown facing a
  player-side entity is assumed NPC — that keeps unverified raiders and unmapped
  pets counting from the first line — and `IdentityRegistry.PlayerVerified`
  triggers deletion of any phantom fights when the assumption proves wrong
  (corrections are queued because the registry is shared across sessions and
  fires on foreign threads). Two unknowns never create a fight.
- **`IdentityRegistry`** (per game server, thread-safe, snapshot-serializable for
  the per-server persistence file): verification evidence from player-only chat
  channels (guild/raid/group/fellowship/tells), raid/group membership lines, /who
  output (new `MembershipEvent`/`WhoEvent` grammars), and pet-leader lines
  (definitive pet→owner). Death grammars feed the NPC set ("died." victims and
  anything slain by the players' side, pets excluded). Class detection and the
  shipped npc/petname lists arrive with the spell DB work.

## Deviations / deferrals (documented on purpose)

- Fight per-actor totals key on the **raw actor name** (pets under their own
  name); owner rollup is query-time per metrics doc §6, so late pet mappings fix
  history without reparse.
- Group and Tell chat senders verify players (the reference's ChatDB verifies
  guild/raid/fellowship); both channels are player-only grammars, so this only
  accelerates verification.
- Merc detection, class detection, and shipped npc/petnames data: deferred to the
  spell-DB phase.

## Verification

`SessionTests` replays a hand-authored raid log through the full pipeline and
asserts hand-computed values: fight boundaries, kill closure, per-actor damage/
tanking totals and hit counts (including a zero-amount dodge), You-resolution to
the log owner, pet mapping via pet-leader line, chat that mimics combat staying
inert, 200 s break grouping, and record-store range queries. `FightTrackerTests`
covers the timeout matrix, assumed-NPC correction, spell-as-attacker windows,
NPC↔NPC/player↔player rejection, and zone closure; `IdentityRegistryTests`
covers classification order, possessive pets, and snapshot round-trips.
