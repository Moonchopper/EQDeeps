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

## Surface

REST: `GET /api/health` · `GET/POST /api/sessions` · `GET/DELETE
/api/sessions/{id}` · `GET /api/sessions/{id}/fights` (with pull-chain group
indices) · `POST /api/sessions/{id}/query` (a QuerySpec body → QueryResult).
Hub `/hubs/live`: `Subscribe`/`Unsubscribe(sessionId)`; server events
`backfill`, `fights`, `tick`.

## Verification

`ServerIntegrationTests` runs the production pipeline on real Kestrel (dynamic
port) with a real SignalR client: session lifecycle + fight DTOs, query
endpoint over HTTP JSON, fight-list pushes on change, and the exit criterion —
scripted appends to a temp log with the median append→client-tick latency
asserted **< 250 ms** (measured ~100 ms locally: ~15–30 ms ingestion poll +
50 ms coalesce + hub delivery).
