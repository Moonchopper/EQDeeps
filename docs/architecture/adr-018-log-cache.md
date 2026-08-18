# ADR-018: A log is parsed once — the records are cached, and the next open resumes

Status: accepted (2026-08-16). Scope: `EQDeeps.Core/Cache/LogCache.cs`,
`Session`, `LogFileIngestion`/`EntryScanner`/`LogBatch`, `StringPool`,
`EQDeeps.Server/LogCacheStore.cs`, `SessionHost`. Issue #59. Revisits the
"index/checkpoint sidecar — deferred" line of ADR-002.

## Context

Every open of a log read the whole file through the parser and rebuilt every
record in memory, and the log only ever grows. ADR-002 measured ingestion at
~1 GB/s and left a sidecar for later, on the grounds that the byte scan was
disk-bound. It was — but the scan is not the pipeline. Measured in Release on
the dev machine with the full `Session` (scan → parse → record store → fight
tracker), on a 512 MB synthetic raid log of 5.5 M lines:

| Stage | Time | Note |
|---|---|---|
| Scan (ADR-002's number) | 0.9 s | disk-bound |
| Parse | 2.2 s | 2.5 M lines/s, CPU-bound |
| Fight tracker + identity | 0.9 s | 6 M records/s |
| **Whole session** | **3.6 s** | 141 MB/s |
| Resident after GC | **1,320 MB** | 2.6× the log |

Two things stood out. Parsing was 60% of a cold open, and the process held
2.6× the log's size in memory — for a 2 GB log, which is an ordinary raider's,
that is ~19 s and ~5 GB, every launch. Half of that memory was strings: the
5.5 M records held **16.4 M string references over 148 distinct strings**,
because the parser is a pure function of one line and allocates
"Fippy Darkpaw" fresh every time it reads it.

The owner's ask (issue #59) was that the user's computer should not re-read
and rebuild everything from scratch, for start-up and for continuous use.

## Decisions

### 1. Pool the strings, per session, after the parser

`StringPool.Canonicalize` rewrites each parsed event so every repeating field
— names, spells, subtypes, zones, owners, items, factions — points at one
instance per distinct value; the parsed copy is gen-0 garbage. Chat text is
left alone (it never repeats; pooling it would just move the copies). The
pool belongs to the session and dies with it, unlike `string.Intern`, which
is process-global and would keep every name from every log ever opened until
exit.

Post-parse rather than inside the grammars on purpose: the parsers stay pure
functions of the message text (the domain doc's invariant, and what makes the
fixture corpus possible), and pooling is one switch in one place instead of a
`StringPool` threaded through thirty substring sites. The cost is a second
allocation per event and a dictionary probe per string field, measured below.

### 2. Cache the records, not the derived state

`LogCache` writes the record stream — timestamp plus typed event — to one file
per log under `%AppData%\EQDeeps\cache\`, and nothing else. Fights, identity,
the query engine's caches, are all functions of the record stream and are
rebuilt by replaying it through exactly the code path a parsed record takes
(`Session.Apply`: identity signals, store, fight tracker). So a resumed session
and a cold one hold the same state — `LogCacheTests` asserts it record-for-
record and fight-for-fight — and a change to how fights close never
invalidates a cache.

The file is stamped with the **module version id of the Core assembly** that
wrote it, and any other MVID rejects it. That is deliberately blunt: a
hand-bumped format version is only bumped when someone remembers, and a
grammar fix nobody remembered to bump for would leave every user on last
month's parse of a line the parser now reads differently. Deterministic builds
make the MVID stable for one build and different for any change to Core, which
is exactly the granularity at which cached parses stop being trustworthy. One
re-parse per upgrade is the price. (`FormatVersion` exists too, for the byte
layout; it will not need bumping while the MVID does the work.)

### 3. Resume by byte offset, validated by content, never by name or size

Ingestion now reports, per batch, the **resume offset** — the byte just past
the last complete line the scanner consumed, which trails the read position
by whatever partial line is being held (`EntryScanner.PendingBytes`). A
checkpoint records that offset with the record count as of the same batch,
captured together under the session gate. The next open restores the records
and starts ingestion at the offset — a line start, so no entry is lost or
duplicated (`ResumeOffsetIsALineStartAndResumingThereLosesNothing`).

Whether the log is still the log the checkpoint describes is decided by a
SHA-256 of the **64 KB immediately before the offset**. EverQuest only appends,
but users trim, archivers rotate, and a reinstalled client writes a fresh file
to the same path; a shorter file or different bytes there means a different
log, and the cache is dropped and rebuilt rather than trusted. A same-length,
same-tail replacement is not a case that occurs. Also rejected: another
path (the header names it), the other parse mode (EMU crit lookbehind changes
what a line means), and any header that does not verify against its own
digest.

Batches also carry a **generation**, bumped when ingestion reopens the file
after truncation or rotation. A session that sees the generation change knows
the records before that point are no longer in the file, and its next
checkpoint resets the cache to describe only the new content
(`ATruncatedLogMidSessionRestartsTheCacheFromTheNewContent`).

### 4. Append-only, header-last, all-or-nothing on read

The file is a fixed 4 KB header region followed by the record stream. A
checkpoint appends the records that arrived since the last one, flushes,
then rewrites the header (record count, data length, offset, fingerprint,
counters, digest) and flushes again. A crash in between leaves a longer file
under an older header, which is simply the previous checkpoint; the torn tail
is truncated on the next open. Strings are written once on first appearance
and referenced by index after — the reader rebuilds the same table in the same
order and interns each entry into the session's pool, so restored records
share instances with what the live parser goes on to produce.

`ReadAll` reads the whole stream before the session applies any of it. A
session that had applied half a cache and then hit a corrupt byte would have
no way to say where in the log the good half ended; whole-or-nothing means a
bad file costs one full parse and nothing worse
(`ACorruptCacheFallsBackToTheParser`).

### 5. When to write, and what may not fail

`SessionHost`'s 1 Hz expiry loop checkpoints as soon as backfill completes —
the write that matters, everything the parser just did — then once a minute
for the live tail, and once more on close (`Program.cs` now disposes the host
after `WaitForShutdownAsync`, which is what closes sessions). The record slice
is copied under the gate (16 bytes a record); serialization is off it. Nothing
about the cache may fail a session: a full disk, a vanished log, a second
session already holding the file (the second opener gets no cache rather than
a shared writer) all degrade to "parse as before".

A session opened with a backfill window restores only the window and never
checkpoints — its store is not the log's records from the top, so a later
resume could not continue them.

`LogCacheStore` (Server) names files `<hash of case-folded full path>-<build>.eqdc`
— **one per log per parser build**. A single file per log would have had a
development build and the installed release taking turns wiping each other's
(each can only read its own; see decision 2), so every open on the owner's
machine would have been cold whenever the two alternated. With one per build,
both stay warm. It takes `--cacheRoot` like every other store, and sweeps on
start-up: a cache whose log is gone, or that nothing has written in 60 days,
or — since every rebuild of Core is a new build with a new file — any foreign
build's cache but the newest for that log, is deleted. That last rule is what
bounds a developer rebuilding twenty times a day to two files per log while
keeping the release's warm.

### 6. The world graph, the same way, one level down

The Map tab's World view had the same shape of problem in miniature: the
graph needs one thing from each of ~1900 map files — the `P` records whose
`to_Zone` text names an exit — and getting them meant reading all 209 MB of
geometry, because a map is one text stream with the labels scattered through
3.2 million segments. Measured on the owner's install: **2.4 s on the first
click after every launch**, 4 ms after that, and gone with the process.

`MapLabelCache` (Server) keeps each map file's labels in
`cache\map-labels-<build>.json` — ~36,000 records, 3.5 MB; per build for the
same reason the log caches are, and swept by the same rule — keyed by full path and
validated per entry against the file's size and last-write time, and the
whole file stamped with the Core build (the label grammar is Core's). The
same principle as the records: cache the expensive *input*, never the derived
answer. `ZoneGraph.Build` runs from the labels every time, so a change to the
graph or the zone table invalidates nothing, a player who edits one map
re-parses one map, and pointing the library at another folder misses cleanly
and switching back is still warm (entries whose files are gone are dropped at
the next write). Measured: **2.4 s → 0.35 s** on the first click, and the
graph the endpoint returns is byte-identical across launches. Not the graph
itself, on purpose — it is small and cheap to build; what was expensive was
reading the files.

Those numbers were taken with the maps folder pinned (`--mapRoot`), and that
turned out to be the one path that hid a second cost. With no folder pinned
the library *discovers* the install — every process on the machine, four
registry hives, every drive — and it did so inside the per-zone file lookup,
so a graph build ran discovery once per zone per map set: 713 times on the
owner's install, ~3.5 ms each. The label cache made no difference to that,
which is why a discovered install still saw **4.4 s cold and 2.6 s warm** on
the first click after every launch (2026-08-17; the release build, measured
three launches in a row against a fresh cache root). The folders are now
resolved once per build and handed to every zone — **1.3 s cold, 0.24 s
warm** — and `MapLibraryTests` pins the resolution count at two per build
(one for the catalogue, one for the graph) so it cannot creep back in.

## Measured (Release, this dev machine, synthetic raid logs)

| Log | Cold, before | Cold, now (pool + write) | Warm (restore) | Cache | Resident before → now |
|---|---|---|---|---|---|
| 512 MB, 5.5 M records | 3.6 s | 4.3 s + 0.6 s write | **1.6 s** | 74 MB (14 B/record) | 1,320 → 613 MB |
| 2 GB, 22 M records | ~18 s | 18.6 s + 2.4 s write | **5.9 s** | 297 MB | ~5.3 GB → 2,448 MB |

So: the first open of a log is ~20% slower than it was (the pool's second
allocation and probes), every open after it is ~3× faster, and the process
holds half the memory it did for the life of the session. Of the warm 5.9 s on
2 GB, ~2 s is reading the cache and ~3.7 s is the fight tracker replaying —
the tracker is now the floor, and the obvious next step if a warm open needs
to be faster still (see below).

Harness: `dotnet run -c Release --project tools/EQDeeps.Bench -- session <log>`
runs a cold open that writes the cache and then a warm one that restores it.

## Rejected alternatives

- **Snapshotting fights and identity too.** Would take a warm 2 GB open from
  ~6 s to ~2 s, but the fight tracker's state (active fights, per-actor
  totals, per-second series, recent player spells, pending corrections,
  current zone) is the layer that changes most, and serializing it couples the
  file to internals that records do not have. Records first; revisit if the
  replay is the complaint. Making the tracker itself cheaper (it locks the
  identity registry twice per damage record and walks the active set once per
  record) helps cold and warm alike and needs no format.
- **A bounded default backfill window** (last N days, extend on demand). No
  format, bounded memory — but it changes what the app shows (the
  all-time views, experience, faction, loot, want the whole log), and it does
  not answer the issue as asked. Still worth having as a knob for the very
  largest logs; orthogonal to this.
- **Interning inside the grammars, by span, before allocating.** Would make
  parsing faster than it was, not slower, by never allocating the repeat
  substrings at all — but it means a span-keyed table (`GetAlternateLookup`
  is .NET 9) and touching every substring site in the crown-jewel parsers. A
  follow-up with its own tests, not a rider on this.
- **A general serializer** (MessagePack, protobuf). A dependency and a
  reflection-shaped format for eighteen record types whose hand codec is 300
  lines, decodes at 10 M records/s, and fails loudly (`NotSupportedException`)
  on a type nobody added a case for — which is what makes "add a case when
  you add an event" enforceable.
- **Fingerprinting the whole file, or its head.** The whole file is what we
  are trying not to read. The head alone misses a trimmed log (same first
  bytes, everything shifted); the tail before the offset catches every case
  the head would and the trim as well.
- **`string.Intern`.** Process-global, never released; three characters'
  logs opened in a day would be three logs' names resident until exit.

## Verification

`LogCacheTests` (Core): every event type round-trips through the codec with
nulls, flags, negatives and a backwards timestamp; appends after a reopen
continue the sequence and the string table; a shrunk log, changed bytes before
the offset, another parser build, the other mode, another path, and garbage
are all rejected while a grown log is accepted; an append without a commit is
not a checkpoint; a corrupt stream throws rather than returning part; a
second opener is refused. Against real sessions: a resumed session matches a
cold one record-for-record and fight-for-fight after the log grows, and again
on the third open; a corrupt cache falls back to the parser and rewrites the
cache; a windowed session restores only the window and does not checkpoint;
a truncation mid-session restarts the cache from the new content and the next
open matches a cold read of the new file; resume offsets are line starts and
resuming at each of them yields exactly the remaining entries; the pool shares
instances and leaves chat text alone.

`MapLabelCacheTests` (Server): a map is served from the cache until its file
changes (proven by rewriting content under a pinned timestamp), the cache
survives a restart and prunes files that are gone, a foreign build's cache
and a corrupt one are ignored and healed, a missing map is null not an error;
and `MapEndpointTests` asserts the graph endpoint writes the label cache under
`--cacheRoot` against the real wiring.

`LogCacheStoreTests` (Server): the path is stable and case-insensitive under
the redirect; archives and held files get no cache; the sweep drops orphans,
old caches and junk and keeps the rest; and through the real API, open →
close → open again restores from the cache the first open wrote, with the
file under `--cacheRoot`. Every server harness now passes `--cacheRoot`.
