# ADR-002: Log ingestion design

Status: accepted (2026-07-31). Scope: phase 2 — the file-reading layer, redesigned
per `log-ingestion-brief.md`.

## Design

One `LogFileIngestion` per opened file (no shared state; N concurrent files is N
instances). Delivery is a bounded `System.Threading.Channels` channel of
`LogBatch`es — the bound is the backpressure mechanism. Phases are in-band:
`Backfill` batches (with bytes-processed/total progress), exactly one
`BackfillComplete` marker, then `Live` batches.

- **Tailing = adaptive polling, not file watching.** Change notifications are
  unreliable for files held open by a writer (the reference app only trusted the
  watcher for delete/rename). We poll at 15 ms while lines flow, backing off
  linearly to 100 ms when idle. Measured file-append→entry-emitted latency:
  **p50 31 ms, p99 33 ms** (budget: 250 ms; reference app: 1–2 s).
- **Byte-level line scanning** (`EntryScanner`): scan chunks for `\n`, carry
  partial tails across reads (a mid-write read never emits a partial line),
  Latin-1 decode (logs are single-byte; stray bytes round-trip), 27-char
  timestamp-prefix memoization for the consecutive-same-second case, `[` probe to
  route glitched double-entry lines through the full splitter. Overlong lines
  (hostile input) and timestamp-less lines are counted and dropped, never thrown.
- **Backfill seek = binary search over byte offsets** (`TimestampSeek`): probe a
  midpoint, align to the next line start, compare its timestamp. The lower bound
  only advances to line starts proven older than the target, so the closing
  linear scan is exact even when probes hit junk; DST regressions degrade to a
  conservative (early) start, never a crash.
- **Truncation/rotation detection** happens at EOF by comparing path-stat vs
  handle-stat: path missing → wait for recreation; path shorter than our position
  → truncated/replaced; path length diverging from a quiet handle → replaced
  (the handle follows a renamed file, so a still-growing renamed original is
  drained before switching). All reopen at offset 0 of the new content — no
  duplicates, because old content never reappears at the path.
- **Gzip archives** run the same scanner over a `GZipStream` sequentially;
  `BackfillFrom` filters entries instead of seeking; live tail is forced off.
- **Clock injection** (`IIngestClock`): the loop's only waits go through it.
  Tests use a yielding `SpinClock` — the whole replay suite (partial writes,
  truncation, rotation, delete/recreate, gzip, seek boundaries, DST) runs on real
  temp files with zero wall-clock sleeps.

## Measured (Release, this dev machine, 512 MB synthetic raid log)

| Metric | Result | Brief target |
|---|---|---|
| Backfill throughput | ~1,015 MB/s (11.0 M entries/s) | ≥ 100 MB/s |
| Allocations | ~397 B/entry | (entry string + action string dominate) |
| Live latency p50 / p99 | 31 / 33 ms | ≤ 250 ms end-to-end |
| Unmatched lines on synthetic corpus | 0 | measured, logged |

Harness: `dotnet run -c Release --project tools/EQDeeps.Bench -- all [MB]`.

## Rejected alternatives

- **FileSystemWatcher-driven reads** — unreliable for modification events on
  files held open; polling at these intervals is cheap and testable.
- **Memory-mapped backfill + parallel parse** — sequential reads already exceed
  the target by 10× and stay disk-bound; parallel chunk parsing adds ordering
  complexity with no current need. Revisit only if record construction (later
  phases) drags throughput below target.
- **Index/checkpoint sidecar** — deferred; binary search makes date-range opens
  fast enough without persistence. Reconsider for instant "resume where I left
  off" UX later.
- **File-identity (creation time / file ID) rotation detection** — Windows
  filesystem tunneling makes recreation inherit creation times, and file IDs need
  P/Invoke; the length-divergence heuristic is portable and covers the real
  cases. Known gap (shared with the reference): a truncate-then-regrow-past-our-
  position race between polls is misread as plain growth; the scanner tolerates
  the resulting mid-line start (counted malformed line).

## Synthetic log generator

`EQDeeps.TestSupport.SyntheticLogGenerator`: deterministic (seeded) raid
scenarios — pull/breather cycles, 5–30 line/sec bursts, melee/DD/DoT/heal/DS/
chat/cast mix, pets, deaths, the double-entry glitch (~1/4000 lines), 1-second
pacing. Generates ~290 MB/s, so multi-GB bench inputs are cheap. Also the basis
for later end-to-end app tests (HANDOFF verification strategy).
