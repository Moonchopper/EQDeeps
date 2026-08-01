using System.IO.Compression;
using System.Text;
using EQDeeps.Core.Ingestion;
using EQDeeps.Core.Parsing;
using EQDeeps.TestSupport;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// Replay-harness tests: real temp files driven by test-side mutations, a
/// spinning virtual clock (no wall-clock sleeps), and bounded awaits on the
/// batch channel. Covers the ingestion brief's verification list.
/// </summary>
public sealed class IngestionTests : IDisposable
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);
    private readonly string _dir;

    public IngestionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string NewFile(string name = "eqlog_Test_server.txt") => Path.Combine(_dir, name);

    private static string Line(int secondsOffset, string action) =>
        SyntheticLogGenerator.Prefix(T0.AddSeconds(secondsOffset)) + action;

    private static async Task<LogBatch> ReadBatchAsync(LogFileIngestion ingestion)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await ingestion.Batches.ReadAsync(timeout.Token);
    }

    /// <summary>Reads batches until <paramref name="count"/> live entries arrive.</summary>
    private static async Task<List<LogEntry>> ReadLiveEntriesAsync(LogFileIngestion ingestion, int count)
    {
        var entries = new List<LogEntry>();
        while (entries.Count < count)
        {
            var batch = await ReadBatchAsync(ingestion);
            Assert.Equal(IngestPhase.Live, batch.Phase);
            entries.AddRange(batch.Entries);
        }

        return entries;
    }

    // ---- backfill ----------------------------------------------------------

    [Fact]
    public async Task BackfillDeliversAllEntriesThenSignalsCompletion()
    {
        var path = NewFile();
        File.WriteAllLines(path, Enumerable.Range(0, 500).Select(i => Line(i, $"An ice giant hits Raider{i % 9:D2} for {i + 1} points of damage.")));

        var ingestion = new LogFileIngestion(path, new IngestOptions { Follow = false });
        await ingestion.RunAsync(CancellationToken.None);

        var entries = new List<LogEntry>();
        var sawComplete = false;
        await foreach (var batch in ingestion.Batches.ReadAllAsync())
        {
            Assert.False(sawComplete, "no batches expected after BackfillComplete");
            if (batch.Phase == IngestPhase.BackfillComplete)
            {
                sawComplete = true;
                Assert.Equal(batch.TotalBytes, batch.BytesProcessed);
            }
            else
            {
                Assert.Equal(IngestPhase.Backfill, batch.Phase);
                entries.AddRange(batch.Entries);
            }
        }

        Assert.True(sawComplete);
        Assert.Equal(500, entries.Count);
        Assert.Equal(T0, entries[0].Timestamp);
        Assert.Equal(T0.AddSeconds(499), entries[^1].Timestamp);
        Assert.Equal("An ice giant hits Raider02 for 480 points of damage.", entries[479].Action);
        Assert.Equal(0, ingestion.MalformedLines);
    }

    [Fact]
    public async Task GlitchedDoubleEntryLineYieldsTwoEntries()
    {
        var path = NewFile();
        File.WriteAllText(path, Line(0, "Test says, 'hello'") + Line(1, "An ice giant died.") + "\r\n");

        var entries = await BackfillAsync(path);
        Assert.Equal(2, entries.Count);
        Assert.Equal("Test says, 'hello'", entries[0].Action);
        Assert.Equal("An ice giant died.", entries[1].Action);
        Assert.Equal(T0.AddSeconds(1), entries[1].Timestamp);
    }

    [Fact]
    public async Task BackfillFromSeeksToFirstEntryAtOrAfterTarget()
    {
        var path = NewFile();
        File.WriteAllLines(path, Enumerable.Range(0, 20_000).Select(i => Line(i, $"Raider01 crushes an ice giant for {i + 1} points of damage.")));

        var target = T0.AddSeconds(14_000);
        var entries = await BackfillAsync(path, new IngestOptions { Follow = false, BackfillFrom = target });

        Assert.Equal(6_000, entries.Count);
        Assert.Equal(target, entries[0].Timestamp);
        Assert.Equal("Raider01 crushes an ice giant for 14001 points of damage.", entries[0].Action);
    }

    [Fact]
    public async Task BackfillFromBeforeFirstLineLoadsEverything()
    {
        var path = NewFile();
        File.WriteAllLines(path, Enumerable.Range(0, 100).Select(i => Line(i, "An ice giant died.")));

        var entries = await BackfillAsync(path, new IngestOptions { Follow = false, BackfillFrom = T0.AddDays(-1) });
        Assert.Equal(100, entries.Count);
    }

    [Fact]
    public async Task BackfillFromAfterLastLineLoadsNothing()
    {
        var path = NewFile();
        File.WriteAllLines(path, Enumerable.Range(0, 100).Select(i => Line(i, "An ice giant died.")));

        var entries = await BackfillAsync(path, new IngestOptions { Follow = false, BackfillFrom = T0.AddDays(1) });
        Assert.Empty(entries);
    }

    [Fact]
    public async Task BackfillFromSurvivesDstBackwardsJump()
    {
        // Timestamps regress by an hour mid-file, then continue.
        var path = NewFile();
        var lines = new List<string>();
        for (var i = 0; i < 5_000; i++)
        {
            lines.Add(Line(i, "Raider01 crushes an ice giant for 10 points of damage."));
        }

        for (var i = 0; i < 5_000; i++)
        {
            lines.Add(Line(i + 5_000 - 3_600, "Raider02 crushes an ice giant for 20 points of damage."));
        }

        File.WriteAllLines(path, lines);

        var target = T0.AddSeconds(5_000 - 3_600 + 2_500);
        var entries = await BackfillAsync(path, new IngestOptions { Follow = false, BackfillFrom = target });

        // Monotonicity is broken, so the exact boundary is fuzzy — but it must not
        // crash and must include the tail of the file from some point at/before
        // the target inside the post-jump run.
        Assert.NotEmpty(entries);
        Assert.Equal(T0.AddSeconds(5_000 - 3_600 + 4_999), entries[^1].Timestamp);
        Assert.True(entries[0].Timestamp <= target, $"scan started late: {entries[0].Timestamp:O}");
    }

    [Fact]
    public async Task GzipArchiveBackfillsThroughSamePipeline()
    {
        var plain = NewFile();
        File.WriteAllLines(plain, Enumerable.Range(0, 300).Select(i => Line(i, $"Raider01 crushes an ice giant for {i + 1} points of damage.")));
        var gzPath = plain + ".gz";
        using (var source = File.OpenRead(plain))
        using (var target = new GZipStream(File.Create(gzPath), CompressionMode.Compress))
        {
            source.CopyTo(target);
        }

        var entries = await BackfillAsync(gzPath, new IngestOptions { Follow = true }); // Follow forced off for .gz
        Assert.Equal(300, entries.Count);

        var filtered = await BackfillAsync(gzPath, new IngestOptions { BackfillFrom = T0.AddSeconds(200) });
        Assert.Equal(100, filtered.Count);
        Assert.Equal(T0.AddSeconds(200), filtered[0].Timestamp);
    }

    private static async Task<List<LogEntry>> BackfillAsync(string path, IngestOptions? options = null)
    {
        var ingestion = new LogFileIngestion(path, options ?? new IngestOptions { Follow = false });
        await ingestion.RunAsync(CancellationToken.None);
        var entries = new List<LogEntry>();
        await foreach (var batch in ingestion.Batches.ReadAllAsync())
        {
            entries.AddRange(batch.Entries);
        }

        return entries;
    }

    // ---- live tail ---------------------------------------------------------

    private sealed record LiveSession(LogFileIngestion Ingestion, Task Run, CancellationTokenSource Cancel) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Cancel.Cancel();
            try
            {
                await Run;
            }
            catch (OperationCanceledException)
            {
            }

            Cancel.Dispose();
        }
    }

    private static async Task<LiveSession> StartLiveAsync(string path)
    {
        var ingestion = new LogFileIngestion(path, new IngestOptions(), new SpinClock());
        var cancel = new CancellationTokenSource();
        var run = Task.Run(() => ingestion.RunAsync(cancel.Token));

        // Drain backfill through the completion marker.
        while (true)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var batch = await ingestion.Batches.ReadAsync(timeout.Token);
            if (batch.Phase == IngestPhase.BackfillComplete)
            {
                break;
            }
        }

        return new LiveSession(ingestion, run, cancel);
    }

    private static void AppendLines(string path, params string[] lines)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream, Encoding.Latin1);
        foreach (var line in lines)
        {
            writer.Write(line);
            writer.Write("\r\n");
        }
    }

    [Fact]
    public async Task LiveAppendsAreDeliveredWithLivePhase()
    {
        var path = NewFile();
        File.WriteAllLines(path, [Line(0, "An ice giant died.")]);

        await using var session = await StartLiveAsync(path);
        AppendLines(path, Line(1, "Raider01 crushes an ice giant for 100 points of damage."), Line(1, "Raider02 kicks an ice giant for 200 points of damage."));

        var entries = await ReadLiveEntriesAsync(session.Ingestion, 2);
        Assert.Equal("Raider01 crushes an ice giant for 100 points of damage.", entries[0].Action);
        Assert.Equal("Raider02 kicks an ice giant for 200 points of damage.", entries[1].Action);
    }

    [Fact]
    public async Task PartialLineIsHeldUntilCompleted()
    {
        var path = NewFile();
        File.WriteAllLines(path, [Line(0, "An ice giant died.")]);

        await using var session = await StartLiveAsync(path);

        var full = Line(1, "Raider01 crushes an ice giant for 100 points of damage.");
        var half = full.Length / 2;
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        using (var writer = new StreamWriter(stream, Encoding.Latin1))
        {
            writer.Write(full[..half]);
        }

        // The half line must not be emitted; completing it must yield exactly the
        // joined line. (If the partial had been emitted, the first entry's action
        // would be a truncated string and this assertion would fail.)
        AppendLines(path, full[half..]);
        var entries = await ReadLiveEntriesAsync(session.Ingestion, 1);
        Assert.Equal("Raider01 crushes an ice giant for 100 points of damage.", entries[0].Action);
        Assert.Equal(0, session.Ingestion.MalformedLines);
    }

    [Fact]
    public async Task TruncationResumesOnNewContentWithoutDuplicates()
    {
        var path = NewFile();
        File.WriteAllLines(path, Enumerable.Range(0, 10).Select(i => Line(i, $"Raider01 crushes an ice giant for {i + 1} points of damage.")));

        await using var session = await StartLiveAsync(path);

        // Truncate to zero (as an in-game /log clear does), then write fresh lines.
        using (var truncate = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
        }

        AppendLines(path, Line(100, "A shadow drake died."));
        var entries = await ReadLiveEntriesAsync(session.Ingestion, 1);
        Assert.Equal("A shadow drake died.", entries[0].Action);
        Assert.Equal(T0.AddSeconds(100), entries[0].Timestamp);
    }

    [Fact]
    public async Task RotationToNewFileResumesOnNewContent()
    {
        var path = NewFile();
        File.WriteAllLines(path, Enumerable.Range(0, 10).Select(i => Line(i, $"Raider01 crushes an ice giant for {i + 1} points of damage.")));

        await using var session = await StartLiveAsync(path);

        // Archive-style rotation: rename the live file, then the game recreates it.
        File.Move(path, path + ".archived");
        File.WriteAllLines(path, [Line(200, "Doomshade died.")]);

        var entries = await ReadLiveEntriesAsync(session.Ingestion, 1);
        Assert.Equal("Doomshade died.", entries[0].Action);
        Assert.Equal(T0.AddSeconds(200), entries[0].Timestamp);
    }

    [Fact]
    public async Task DeletedFileWaitsForRecreation()
    {
        var path = NewFile();
        File.WriteAllLines(path, [Line(0, "An ice giant died.")]);

        await using var session = await StartLiveAsync(path);

        File.Delete(path);
        File.WriteAllLines(path, [Line(300, "Grendish the Crusader died.")]);

        var entries = await ReadLiveEntriesAsync(session.Ingestion, 1);
        Assert.Equal("Grendish the Crusader died.", entries[0].Action);
    }

    [Fact]
    public async Task SyntheticRaidLogRoundTripsWithoutMalformedLines()
    {
        var path = NewFile();
        var generator = new SyntheticLogGenerator(seed: 7, playerCount: 54, start: T0);
        generator.WriteFile(path, targetBytes: 2_000_000);

        var ingestion = new LogFileIngestion(path, new IngestOptions { Follow = false });
        await ingestion.RunAsync(CancellationToken.None);
        long count = 0;
        await foreach (var batch in ingestion.Batches.ReadAllAsync())
        {
            count += batch.Entries.Count;
        }

        Assert.True(count > 10_000, $"expected a raid-scale entry count, got {count}");
        Assert.Equal(0, ingestion.MalformedLines);
        Assert.Equal(0, ingestion.OverlongLinesDropped);
    }
}
