using System.Diagnostics;
using System.Text;
using EQDeeps.Core.Ingestion;
using EQDeeps.TestSupport;

// Ingestion benchmark harness (see docs/architecture/log-ingestion-brief.md):
//   gen <path> <MB>       generate a synthetic raid log
//   backfill <path>       measure backfill throughput + allocations
//   latency [samples]     measure file-append -> entry-emitted latency
//   all [MB]              gen (temp) + backfill + latency in one run

var command = args.Length > 0 ? args[0] : "all";
switch (command)
{
    case "gen":
        Generate(args[1], long.Parse(args[2]) << 20);
        break;
    case "backfill":
        await BackfillAsync(args[1]);
        break;
    case "latency":
        await LatencyAsync(args.Length > 1 ? int.Parse(args[1]) : 200);
        break;
    case "all":
    {
        var mb = args.Length > 1 ? long.Parse(args[1]) : 512;
        var path = Path.Combine(Path.GetTempPath(), "eqdeeps-bench.log");
        Generate(path, mb << 20);
        await BackfillAsync(path);
        await LatencyAsync(200);
        File.Delete(path);
        break;
    }
    default:
        Console.Error.WriteLine("usage: gen <path> <MB> | backfill <path> | latency [samples] | all [MB]");
        return 1;
}

return 0;

static void Generate(string path, long bytes)
{
    var sw = Stopwatch.StartNew();
    var written = new SyntheticLogGenerator(seed: 99).WriteFile(path, bytes);
    Console.WriteLine($"gen: {written / 1048576.0:F0} MB in {sw.Elapsed.TotalSeconds:F1}s ({written / 1048576.0 / sw.Elapsed.TotalSeconds:F0} MB/s) -> {path}");
}

static async Task BackfillAsync(string path)
{
    var ingestion = new LogFileIngestion(path, new IngestOptions { Follow = false });
    long entries = 0, bytes = new FileInfo(path).Length;
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var sw = Stopwatch.StartNew();

    var run = ingestion.RunAsync(CancellationToken.None);
    await foreach (var batch in ingestion.Batches.ReadAllAsync())
    {
        entries += batch.Entries.Count;
    }

    await run;
    sw.Stop();
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

    Console.WriteLine(
        $"backfill: {bytes / 1048576.0:F0} MB, {entries:N0} entries in {sw.Elapsed.TotalSeconds:F2}s " +
        $"({bytes / 1048576.0 / sw.Elapsed.TotalSeconds:F0} MB/s, {entries / sw.Elapsed.TotalSeconds / 1e6:F1}M entries/s, " +
        $"{(double)allocated / entries:F0} B alloc/entry, malformed={ingestion.MalformedLines})");
}

static async Task LatencyAsync(int samples)
{
    var path = Path.Combine(Path.GetTempPath(), $"eqdeeps-latency-{Environment.ProcessId}.log");
    File.WriteAllText(path, SyntheticLogGenerator.Prefix(DateTime.Now) + "An ice giant died.\r\n");

    var ingestion = new LogFileIngestion(path, new IngestOptions());
    using var cancel = new CancellationTokenSource();
    var run = Task.Run(() => ingestion.RunAsync(cancel.Token));

    var reader = ingestion.Batches;
    while ((await reader.ReadAsync()).Phase != IngestPhase.BackfillComplete)
    {
    }

    await using var appendStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    await using var writer = new StreamWriter(appendStream, Encoding.Latin1) { AutoFlush = true };

    var latencies = new List<double>(samples);
    for (var i = 0; i < samples; i++)
    {
        var sw = Stopwatch.StartNew();
        writer.WriteLine(SyntheticLogGenerator.Prefix(DateTime.Now) + $"Raider01 crushes an ice giant for {i + 1} points of damage.");
        var batch = await reader.ReadAsync();
        sw.Stop();
        if (batch.Entries.Count != 1)
        {
            Console.Error.WriteLine($"unexpected batch of {batch.Entries.Count}");
        }

        latencies.Add(sw.Elapsed.TotalMilliseconds);
        await Task.Delay(5); // let the poll interval reset between samples
    }

    cancel.Cancel();
    try
    {
        await run;
    }
    catch (OperationCanceledException)
    {
    }

    File.Delete(path);
    latencies.Sort();
    Console.WriteLine(
        $"latency: n={samples} p50={latencies[samples / 2]:F1}ms p95={latencies[(int)(samples * 0.95)]:F1}ms " +
        $"p99={latencies[(int)(samples * 0.99)]:F1}ms max={latencies[^1]:F1}ms");
}
