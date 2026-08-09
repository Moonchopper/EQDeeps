# ADR-011: Gear snapshots

Status: **withdrawn (2026-08-09)**, superseded by nothing. Accepted 2026-08-03,
shipped in v0.7.0, removed entirely in v0.9.4. Scope: feature F24.

> This file is kept rather than deleted so the ADR sequence has no hole and so
> the next person to propose gear tracking finds out what happened last time.
> The original design is in git history at `v0.9.3`.

## What it was

The app read the file produced by `/outputfile inventory`, recorded each
distinct version as a snapshot, marked the changes on time charts, and organised
an Overview tab around **sets** — a snapshot plus the stretch it was worn for —
so a player could see how each set actually played.

## Why it was accepted

Gear is the commonest reason a player's DPS moves, and the original ADR
established that it is *nearly* unknowable from the client: loadouts on EQ
Legends are class loadouts whose equipment lives server-side, a swap emits no
log line, equipping emits no log line, and no client-side file records what is
worn. That investigation survives this ADR's withdrawal in
[eq-legends-loadouts.md](../domain/eq-legends-loadouts.md) §4, which lists every
file checked and what it actually holds. The single exception is the manual `/outputfile inventory` dump. The
decision was to accept that manual capture and be loud about its cost — never
issue the command, report how much combat had happened since the last proof, and
show gear it could not vouch for as unknown.

## Why it was withdrawn

**The mitigations were the tell.** A feature that has to report "N fights since
the last proof" beside every number, and fall back to "gear unknown" for any
frame older than the first dump, is one whose underlying signal cannot carry the
claims the UI makes with it. Every figure was really "true as of whenever you
last remembered to type the command", while presenting with the same authority
as a parse — which is a worse failure than not showing it, because a stale
number that looks measured gets acted on.

Owner's call, and the right one: *"because we can't reliably keep gear up to
date, I don't feel it has sufficient trustworthiness."*

Note this is a different kind of problem from the unlogged instance settings in
[ADR-012](adr-012-mob-health.md) or the unlogged loadout swaps in
[ADR-013](adr-013-incoming-damage.md). Those are things the log does not say, and
both features answer by widening a band or splitting a key — the estimate stays
honest because the uncertainty is *expressed*. Gear had no equivalent: the
uncertainty is unbounded (a dump can be arbitrarily old and arbitrarily wrong)
and there is nothing to express it against.

## What was removed

The tab and set comparison, the gear-change marks on every time chart,
`GET /api/sessions/{id}/gear`, the `gear` SignalR event, `GearWatcher`,
`GearStore`, `Core/Gear/` including the inventory-dump parser, `--gearRoot`,
the gear tests and fixture, the screenshot staging, and
`docs/domain/inventory-file-format.md`.

Snapshots already on disk under `%AppData%\EQDeeps\gear\` are left alone and
ignored. They are the one thing the app persisted that a log cannot recreate, so
they are not deleted — but nothing writes there again.

## If this is revisited

The blocker is unchanged and it is not an engineering one: **there is no
automatic source of equipped gear on this client.** Anything built on the manual
dump inherits exactly the problem that got this removed. A future attempt needs
either a game-side signal that does not exist today, or an explicit design in
which the user understands they are reading a hand-maintained record rather than
a parse — and in which nothing derived from it is presented beside measured
numbers as though it were one.
