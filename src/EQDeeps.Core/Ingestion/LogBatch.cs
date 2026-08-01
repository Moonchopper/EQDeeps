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
/// </summary>
public sealed record LogBatch(
    IngestPhase Phase,
    IReadOnlyList<LogEntry> Entries,
    long BytesProcessed,
    long? TotalBytes);
