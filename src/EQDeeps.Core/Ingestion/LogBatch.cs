using EQDeeps.Core.Parsing;

namespace EQDeeps.Core.Ingestion;

public enum IngestPhase
{
    /// <summary>Historical entries, delivered as fast as the disk allows.</summary>
    Backfill,

    /// <summary>
    /// In-band switchover marker: always emitted exactly once (possibly with zero
    /// entries) so consumers can defer recompute until history is complete.
    /// </summary>
    BackfillComplete,

    /// <summary>Entries observed while tailing.</summary>
    Live,
}

/// <summary>
/// A batch of timestamped entries from one file. <see cref="BytesProcessed"/> /
/// <see cref="TotalBytes"/> drive progress UI during backfill; TotalBytes is null
/// once live (the file grows forever).
///
/// <para><see cref="ResumeOffset"/> is the byte offset just past the last
/// complete line the batch's entries came from — a line start, where a later
/// reader could reopen the file and pick up without duplicating or losing an
/// entry. It trails <see cref="BytesProcessed"/> by the partial line the
/// scanner is holding back, and is what a checkpoint records. -1 when the
/// source has no resumable offsets (a gzip archive is read front to back).
/// <see cref="Generation"/> counts the file's reopens after truncation or
/// rotation: entries in generation 1 came from different content than
/// generation 0, so offsets across generations do not compare, and a
/// checkpoint that spans one would describe bytes that no longer exist.</para>
/// </summary>
public sealed record LogBatch(
    IngestPhase Phase,
    IReadOnlyList<LogEntry> Entries,
    long BytesProcessed,
    long? TotalBytes,
    long ResumeOffset = -1,
    int Generation = 0);
