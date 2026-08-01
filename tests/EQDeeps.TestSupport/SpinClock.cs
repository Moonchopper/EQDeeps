using EQDeeps.Core.Ingestion;

namespace EQDeeps.TestSupport;

/// <summary>
/// A clock whose delays complete immediately (yielding the scheduler): the tail
/// loop polls continuously without wall-clock sleeps, so ingestion tests are
/// driven purely by file mutations and channel reads.
/// </summary>
public sealed class SpinClock : IIngestClock
{
    /// <summary>Total delays requested — lets tests observe idle polling happened.</summary>
    public long DelayCount;

    public async Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref DelayCount);
        await Task.Yield();
    }
}
