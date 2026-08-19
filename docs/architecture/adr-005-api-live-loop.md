# ADR-005: API layer and live loop

Status: accepted (2026-08-01). Scope: phase 5 — the local backend host, REST
surface, realtime channel, live meter tick.

## Decisions

- **SignalR over raw WebSockets.** It ships inside ASP.NET Core (MIT, no new
  dependency weight), and gives reconnection, per-session groups, and typed
  invocation for free; the SPA uses `@microsoft/signalr` (MIT). Raw WS would
  mean rebuilding reconnect/backoff/framing for no gain at localhost scale.
  Payloads use the same camelCase+string-enum JSON as the REST API.
- **Localhost only.** Kestrel binds `http://127.0.0.1:5487` unless overridden
  via `--urls`/`ASPNETCORE_URLS`; the server is never a network service.
- **Concurrency = one gate per session.** Session state mutates on its single
  processing task; queries and DTO builds from request threads take
  `Session.Gate`. Batches hold the lock briefly (they're already coalesced by
  ingestion), so contention is negligible at raid scale. Rejected: a
  reader-writer scheme or immutable snapshots — premature for the measured load.
- **Push = coalescing loop, not per-line sends.** `Session.BatchProcessed`
  signals a per-session loop that waits 50 ms to swallow bursts, then pushes to
  the session's SignalR group: `backfill` progress, `fights` snapshots (on
  fight-version change), and `tick` — the live meter as an ordinary QuerySpec
  (damage by player over the currently active fights) run through the same
  cached query engine as every other view.
- **Wall-clock fight expiry.** Log time drives everything during replay, but a
  fight's 30 s timeout must also fire when the log goes quiet; a 1 Hz timer
  calls `ExpireFights(DateTime.Now)` after backfill (log timestamps are local
  time) and pushes the fight list when anything closed.
- **`fights` is a delta, not a snapshot (2026-08-17).** The snapshot was the
  whole list on every version change — on the owner's 8,000-fight log, 2 MB
  once a second in combat, serialised, sent, parsed and reconciled, for one
  fight's totals moving. Every `Fight` now carries the tracker version of its
  last change; a push is `{version, baseVersion, full, fights}` and carries
  only fights changed after `baseVersion`, which the client merges by id.
  `full` is sent when a delta cannot say what happened — nothing pushed yet, a
  fight removed (a name reclassified as a player takes its fights with it),
  or the learned-health snapshot replaced, which moves every fight's
  estimate. `GET …/fights` returns `{version, fights}` so the client knows
  what its list is a snapshot of; a delta applies only if `baseVersion` is at
  or before that, else the client refetches, and a fetched snapshot never
  replaces a list already merged past its version. Measured: 2 MB → a few KB
  per push, and the client's per-push work went with it.

## Surface

REST: `GET /api/health` · `GET/POST /api/sessions` · `GET/DELETE
/api/sessions/{id}` · `GET /api/sessions/{id}/fights` (`{version, fights}`,
with pull-chain group indices) · `POST /api/sessions/{id}/query` (a QuerySpec
body → QueryResult).
Hub `/hubs/live`: `Subscribe`/`Unsubscribe(sessionId)`; server events
`backfill`, `fights`, `tick`.

## Verification

`ServerIntegrationTests` runs the production pipeline on real Kestrel (dynamic
port) with a real SignalR client: session lifecycle + fight DTOs, query
endpoint over HTTP JSON, fight-list pushes on change, and the exit criterion —
scripted appends to a temp log with the median append→client-tick latency
asserted **< 250 ms** (measured ~100 ms locally: ~15–30 ms ingestion poll +
50 ms coalesce + hub delivery).
