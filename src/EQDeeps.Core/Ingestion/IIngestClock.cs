namespace EQDeeps.Core.Ingestion;

/// <summary>
/// The tail loop's only source of waiting. Injected so tests drive the loop
/// without wall-clock sleeps (see the ingestion brief's replayability rule).
/// </summary>
public interface IIngestClock
{
    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Production clock: real delays.</summary>
public sealed class SystemClock : IIngestClock
{
    public static readonly SystemClock Instance = new();

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
