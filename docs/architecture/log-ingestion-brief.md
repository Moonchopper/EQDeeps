# Log Ingestion — Design Brief (fresh eyes wanted)

The product owner explicitly wants the file-reading layer **redesigned from first principles**, not inherited. This brief states the requirements, describes the reference implementation's approach purely as a baseline, and marks the open design space. Deliverable: your design (short ADR + code) that beats the baseline on latency, backfill throughput, and testability.

## Requirements (hard)

1. **Tail a file EverQuest is writing.** EQ holds a write handle; open with `FileShare.ReadWrite | FileShare.Delete`. Appends are line-oriented but a read can catch a partial line mid-write — never emit a partial line.
2. **Survive truncation and rotation.** Users clear logs in-game (length shrinks); archiving tools rename/move the file and EQ recreates it. Detect both; resume cleanly on the new content without duplicates or crashes.
3. **Historical backfill of multi-GB files, fast.** "Load last N hours/days" must not read the whole file: log timestamps are ordered, so a **binary search over byte offsets** (probe, align to next line, compare timestamp) finds the start point. Full-file loads should be disk-bound, not parser-bound.
4. **Gzip archives** readable through the same pipeline (sequential only — no seek; fine).
5. **N concurrent files** (multi-character), independent lifecycles, no shared mutable state between them.
6. **Two-phase delivery:** backfill (historical, as fast as possible, progress-reported) then live (latency-sensitive). Consumers must know which phase a line belongs to and when the switchover happens.
7. **Backpressure.** A slow consumer must not balloon memory unboundedly; a fast producer must not starve the UI thread pool.
8. **Replayable for tests.** The pipeline must run against a fixture file with a virtual clock — including simulated appends, truncation, and rotation — with no real EverQuest and no wall-clock sleeps in tests.
9. **Timestamp parsing at ingest.** Lines carry a fixed-width 27-char `[ctime] ` prefix (see domain doc §2); the ingest layer attaches the parsed epoch-seconds. Exploit the "consecutive lines usually share a timestamp" property (the reference memoizes the previous timestamp string — keep an equivalent or better trick).

## Baseline: what the reference implementation does (for calibration only)

EQLogParser's `LogReader` (single class, ~250 lines): `FileStream` (144 KB buffer, async, sequential-scan hint) + `StreamReader`; on open, either seek-to-end (live only) or binary-search seek for `minBack` minutes of history; then a loop of "read all available lines → sleep 200 ms"; a `FileSystemWatcher` on the directory flags delete/rename, and `length < position` flags truncation — both trigger reopen-and-seek-to-end. Lines go into a bounded `BlockingCollection` (capacity 100k, consumed in batches of 5000) feeding one consumer task. Characteristics: simple and robust; 200 ms worst-case tail latency before parsing even starts; byte→string allocation per line; single consumer; progress via bytes-read ratio.

Total end-to-end in the old app (tail poll + UI batching timers) was 1–2 s. Our target is ≤ 250 ms file-to-push, and backfill ≥ 100 MB/s.

## Open design space (explore; choose deliberately)

- **Read mechanics:** `ReadFileAsync`-style polling vs `FileSystemWatcher`-driven reads (watcher events for *change* are unreliable on files held open — verify empirically; the reference only trusted it for delete/rename) vs overlapped reads on a short adaptive interval (e.g., poll fast while lines are flowing, back off when idle).
- **Zero/low-allocation path:** `System.IO.Pipelines` or raw `FileStream` + pooled byte buffers, scanning for `\n` in bytes, parsing records from `ReadOnlySpan<byte>`/`Span<char>` without materializing a string per line. The grammars are anchored keyword scans — `SearchValues`, vectorized `IndexOf`, and source-generated regex (if regex at all) apply. Measure; don't assume.
- **Backfill fast path:** memory-mapped file or large sequential reads + parallel *parse* (chunk by line boundaries, parse chunks concurrently, reassemble in order — records are independent; only fight-state application needs ordering). This is the biggest potential win over the baseline.
- **Index/checkpoint sidecar:** optional per-file sidecar (e.g., `%AppData%` cache keyed by file identity) storing byte-offset↔timestamp waypoints and a last-processed watermark — instant "resume where I left off" and instant date-range seeks on reopen. Handle file-truncated/replaced invalidation.
- **Delivery:** `System.Threading.Channels` (bounded) per file feeding the parser; per-second batch flush to downstream (matches log resolution and the push granularity in system-overview.md).
- **Clock abstraction:** inject a clock/scheduler so tests drive time; expose the tail loop's waits through it.

## Deliverables & verification

1. Short ADR: chosen design, rejected alternatives, and why.
2. Benchmarks (BenchmarkDotNet or a simple harness) on a generated multi-GB synthetic log: backfill MB/s, allocations/line, live-append latency (file write → record emitted).
3. Test suite using the replay harness: partial-line writes, truncation mid-tail, rotation mid-tail, gz backfill, binary-search seek correctness at boundaries (first line, last line, timestamp gaps, DST backwards jump).
4. The synthetic log generator itself (also used by end-to-end app tests — see HANDOFF.md).
