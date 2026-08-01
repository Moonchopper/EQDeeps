namespace EQDeeps.Core.Ingestion;

public sealed record IngestOptions
{
    /// <summary>
    /// Start of the historical window; null loads the entire file. For plain
    /// files this seeks via binary search over byte offsets; for gzip archives
    /// (sequential-only) earlier entries are read and discarded.
    /// </summary>
    public DateTime? BackfillFrom { get; init; }

    /// <summary>Tail the file live after backfill. Ignored (forced off) for gzip archives.</summary>
    public bool Follow { get; init; } = true;

    /// <summary>Poll interval while lines are flowing.</summary>
    public TimeSpan ActivePollInterval { get; init; } = TimeSpan.FromMilliseconds(15);

    /// <summary>Ceiling the poll interval backs off to when the file is quiet.</summary>
    public TimeSpan IdlePollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Read-chunk size; backfill throughput wants big sequential reads.</summary>
    public int ReadBufferSize { get; init; } = 1 << 20;

    /// <summary>
    /// Bounded batch-channel capacity — the backpressure knob: a slow consumer
    /// stalls the reader instead of ballooning memory.
    /// </summary>
    public int ChannelCapacity { get; init; } = 64;

    /// <summary>Lines longer than this are dropped (counted, never thrown) — hostile-input bound.</summary>
    public int MaxLineLength { get; init; } = 64 * 1024;
}
