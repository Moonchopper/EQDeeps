using System.Text;
using EQDeeps.Core.Cache;
using EQDeeps.Core.Events;
using EQDeeps.Core.Ingestion;
using EQDeeps.Core.Sessions;
using EQDeeps.TestSupport;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// The log cache (issue #59, ADR-018): a resumed session must be
/// indistinguishable from a cold one, and every way the cache could be wrong
/// about the log must send it back to the parser rather than to stale
/// records.
/// </summary>
public sealed class LogCacheTests : IDisposable
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);
    private readonly string _dir;

    public LogCacheTests()
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

    private string LogPath(string name = "eqlog_Kizant_xegony.txt") => Path.Combine(_dir, name);

    private string CachePath(string name = "cache.eqdc") => Path.Combine(_dir, name);

    private static string Line(int t, string action) => SyntheticLogGenerator.Prefix(T0.AddSeconds(t)) + action;

    // ---- codec ----

    /// <summary>One of every event type, exercising nulls, flags, and negatives.</summary>
    private static TimedRecord[] OneOfEverything()
    {
        var t = T0;
        return
        [
            new(t, new DamageEvent("Raider01", "An ice giant", 1234, DamageKind.Melee, "Crushes", HitModifiers.Critical | HitModifiers.Flurry)),
            new(t, new DamageEvent(null, "Raider02", 0, DamageKind.Dodge, null)),
            new(t = t.AddSeconds(1), new DamageEvent("Burst of Flames", "An ice giant", 4_000_000_000u, DamageKind.Other, "Burst of Flames", HitModifiers.None, AttackerIsSpell: true, AttackerOwner: "Raider03", DefenderOwner: null, School: "fire")),
            new(t, new HealEvent("Raider04", "Raider01", 500, 750, false, "Blessing", HitModifiers.Lucky, "Owner")),
            new(t, new HealEvent(null, "Raider01", 1, 1, true, null)),
            new(t = t.AddSeconds(90), new DeathEvent("An ice giant", "Raider01")),
            new(t, new DeathEvent("Raider05", null)),
            new(t, new CastEvent("Raider06", "Selo's Accelerando", CastKind.Begin, Song: true)),
            new(t, new CastEvent("Raider06", null, CastKind.Fizzle)),
            new(t, new WearOffEvent("Spirit of Wolf", "Kizant")),
            new(t, new AbilityEvent("Kizant", "Rest")),
            new(t, new StanceEvent("Kizant", "Defensive")),
            new(t, new TauntEvent("Raider07", "An ice giant", true, Improved: true)),
            new(t, new ChatEvent(ChatChannel.Tell, "Raider08", "hi there, 'quoted' é", "Kizant")),
            new(t, new ChatEvent(ChatChannel.Custom, "Raider08", "hello", null, "General")),
            new(t, new ZoneEvent("Plane of Fear")),
            new(t, new ZoneEvent(null, Welcome: true)),
            new(t, new MembershipEvent("Raider09", Raid: true, Joined: false)),
            new(t, new WhoEvent("Raider10", 60, "High Priest")),
            new(t, new WhoEvent("Raider11", null, null)),
            new(t, new ResistEvent("Kizant", "An ice giant", "Ice Comet")),
            new(t, new ResistEvent("Kizant", null, "Ice Comet")),
            new(t, new ExperienceEvent(1.812, Party: true)),
            new(t, new ExperienceEvent(null, Party: false, AaPoint: true, AaTotal: 42)),
            new(t, new FactionEvent("Wolves of the North", -4, Better: false)),
            new(t, new FactionEvent("Wolves of the North", null, Better: true, Capped: true)),
            new(t, new LootEvent("Kizant", "Cold-Forged Cudgel", "a froglok ton knight", null, 2)),
            new(t, new LootEvent("Kizant", null, "split", 12_345_678_901L)),
            new(t, new MerchantEvent("Didek Stormhammer", "Rusty Two Handed Sword +2", 1, 259, Sold: true)),
            new(t, new MerchantEvent("Storn Trueblade", "Spell: Holy Armor", 20, 50, Sold: false)),
            new(t, new ConsiderEvent("An ice giant", "scowls at you, ready to attack", 55)),
            new(t, new ConsiderEvent("A rat", "regards you indifferently", null)),
            new(t = t.AddSeconds(-30), new LevelEvent(42)), // a DST-style step backwards
            new(t.AddDays(3), new DamageEvent("Raider01", "An ice giant", 1, DamageKind.Melee, "Crushes")),
        ];
    }

    [Fact]
    public void EveryEventTypeRoundTrips()
    {
        var log = LogPath();
        File.WriteAllText(log, "0123456789\n");
        var records = OneOfEverything();

        using (var cache = LogCache.Open(CachePath(), log, emuMode: false))
        {
            cache.Append(records);
            cache.Commit(new CacheCheckpoint(11, records.Length, 3, 1, 2, 0, "Raider99"));
        }

        using var reopened = LogCache.Open(CachePath(), log, emuMode: false);
        var checkpoint = Assert.IsType<CacheCheckpoint>(reopened.Checkpoint);
        Assert.Equal(11, checkpoint.ResumeOffset);
        Assert.Equal(records.Length, checkpoint.RecordCount);
        Assert.Equal(3, checkpoint.UnrecognizedLines);
        Assert.Equal(1, checkpoint.ParserFailures);
        Assert.Equal(2, checkpoint.MalformedLines);
        Assert.Equal("Raider99", checkpoint.PendingEmuCritAttacker);

        var pool = new StringPool();
        var restored = reopened.ReadAll(pool);
        Assert.Equal(records.Length, restored.Length);
        for (var i = 0; i < records.Length; i++)
        {
            Assert.Equal(records[i].Timestamp, restored[i].Timestamp);
            Assert.Equal(records[i].Event, restored[i].Event); // record structural equality
        }

        // Repeating strings came back as one instance each, and pooled.
        var first = (DamageEvent)restored[0].Event;
        var last = (DamageEvent)restored[^1].Event;
        Assert.Same(first.Attacker, last.Attacker);
        Assert.Same(first.Defender, last.Defender);
        Assert.Same(first.Attacker, pool.Intern("Raider01"));
    }

    [Fact]
    public void AppendsAfterReopenContinueTheSequence()
    {
        var log = LogPath();
        File.WriteAllText(log, new string('x', 200) + "\n");
        var all = OneOfEverything();

        using (var cache = LogCache.Open(CachePath(), log, emuMode: false))
        {
            cache.Append(all.AsSpan(0, 10));
            cache.Commit(new CacheCheckpoint(100, 10, 0, 0, 0, 0, null));
        }

        using (var cache = LogCache.Open(CachePath(), log, emuMode: false))
        {
            Assert.Equal(10, cache.Checkpoint!.RecordCount);
            _ = cache.ReadAll(new StringPool());
            cache.Append(all.AsSpan(10));
            cache.Commit(new CacheCheckpoint(201, all.Length, 0, 0, 0, 0, null));
        }

        using var reopened = LogCache.Open(CachePath(), log, emuMode: false);
        var restored = reopened.ReadAll(new StringPool());
        Assert.Equal(all.Select(r => r.Event), restored.Select(r => r.Event));
        Assert.Equal(all.Select(r => r.Timestamp), restored.Select(r => r.Timestamp));
    }

    // ---- validation ----

    private void WriteCheckpointedCache(string log, string cache, Guid? version = null, bool emu = false)
    {
        var records = OneOfEverything();
        using var c = LogCache.Open(cache, log, emu, version);
        c.Append(records);
        c.Commit(new CacheCheckpoint(new FileInfo(log).Length, records.Length, 0, 0, 0, 0, null));
    }

    [Fact]
    public void RejectsWhenTheLogHasShrunk()
    {
        var log = LogPath();
        File.WriteAllLines(log, Enumerable.Range(0, 100).Select(i => Line(i, "An ice giant died.")));
        WriteCheckpointedCache(log, CachePath());

        File.WriteAllLines(log, Enumerable.Range(0, 50).Select(i => Line(i, "An ice giant died.")));
        using var cache = LogCache.Open(CachePath(), log, emuMode: false);
        Assert.Null(cache.Checkpoint);
    }

    [Fact]
    public void RejectsWhenTheBytesBeforeTheOffsetChanged()
    {
        var log = LogPath();
        File.WriteAllLines(log, Enumerable.Range(0, 100).Select(i => Line(i, "An ice giant died.")));
        WriteCheckpointedCache(log, CachePath());

        // Same length, different content: a different character's log copied
        // over this one, say.
        File.WriteAllLines(log, Enumerable.Range(0, 100).Select(i => Line(i, "An ice giant dies.")));
        using var cache = LogCache.Open(CachePath(), log, emuMode: false);
        Assert.Null(cache.Checkpoint);
    }

    [Fact]
    public void AcceptsWhenTheLogHasOnlyGrown()
    {
        var log = LogPath();
        File.WriteAllLines(log, Enumerable.Range(0, 100).Select(i => Line(i, "An ice giant died.")));
        WriteCheckpointedCache(log, CachePath());

        File.AppendAllLines(log, Enumerable.Range(100, 100).Select(i => Line(i, "An ice giant died.")));
        using var cache = LogCache.Open(CachePath(), log, emuMode: false);
        Assert.NotNull(cache.Checkpoint);
    }

    [Fact]
    public void RejectsACacheFromAnotherParserBuild()
    {
        var log = LogPath();
        File.WriteAllLines(log, [Line(0, "An ice giant died.")]);
        WriteCheckpointedCache(log, CachePath(), version: Guid.NewGuid());

        using var cache = LogCache.Open(CachePath(), log, emuMode: false);
        Assert.Null(cache.Checkpoint);
    }

    [Fact]
    public void RejectsACacheParsedInTheOtherMode()
    {
        var log = LogPath();
        File.WriteAllLines(log, [Line(0, "An ice giant died.")]);
        WriteCheckpointedCache(log, CachePath(), emu: true);

        using var cache = LogCache.Open(CachePath(), log, emuMode: false);
        Assert.Null(cache.Checkpoint);
    }

    [Fact]
    public void RejectsACacheOfAnotherLog()
    {
        var log = LogPath();
        var other = LogPath("eqlog_Other_xegony.txt");
        File.WriteAllLines(log, [Line(0, "An ice giant died.")]);
        File.Copy(log, other);
        WriteCheckpointedCache(other, CachePath());

        // Identical bytes, but the header names a different path.
        using var cache = LogCache.Open(CachePath(), log, emuMode: false);
        Assert.Null(cache.Checkpoint);
    }

    [Fact]
    public void GarbageIsReplacedByAnEmptyCache()
    {
        var log = LogPath();
        File.WriteAllLines(log, [Line(0, "An ice giant died.")]);
        File.WriteAllBytes(CachePath(), Encoding.ASCII.GetBytes(new string('!', 10_000)));

        using var cache = LogCache.Open(CachePath(), log, emuMode: false);
        Assert.Null(cache.Checkpoint);
        cache.Append(OneOfEverything().AsSpan(0, 3));
        cache.Commit(new CacheCheckpoint(new FileInfo(log).Length, 3, 0, 0, 0, 0, null));
        cache.Dispose();

        using var reopened = LogCache.Open(CachePath(), log, emuMode: false);
        Assert.Equal(3, reopened.Checkpoint!.RecordCount);
    }

    [Fact]
    public void AnAppendWithoutACommitIsNotACheckpoint()
    {
        var log = LogPath();
        File.WriteAllLines(log, Enumerable.Range(0, 100).Select(i => Line(i, "An ice giant died.")));
        var all = OneOfEverything();

        using (var cache = LogCache.Open(CachePath(), log, emuMode: false))
        {
            cache.Append(all.AsSpan(0, 5));
            cache.Commit(new CacheCheckpoint(new FileInfo(log).Length, 5, 0, 0, 0, 0, null));
            cache.Append(all.AsSpan(5)); // the process dies here
        }

        using var reopened = LogCache.Open(CachePath(), log, emuMode: false);
        Assert.Equal(5, reopened.Checkpoint!.RecordCount);
        Assert.Equal(5, reopened.ReadAll(new StringPool()).Length);
    }

    [Fact]
    public void ACorruptRecordStreamThrowsRatherThanReturningPart()
    {
        var log = LogPath();
        File.WriteAllLines(log, Enumerable.Range(0, 100).Select(i => Line(i, "An ice giant died.")));
        WriteCheckpointedCache(log, CachePath());

        // Scribble over the record stream past the header, leaving the header
        // (and its digest) intact.
        using (var f = new FileStream(CachePath(), FileMode.Open, FileAccess.ReadWrite))
        {
            f.Seek(4096 + 8, SeekOrigin.Begin);
            f.Write(new byte[64]);
        }

        using var cache = LogCache.Open(CachePath(), log, emuMode: false);
        Assert.NotNull(cache.Checkpoint);
        Assert.ThrowsAny<Exception>(() => cache.ReadAll(new StringPool()));
    }

    [Fact]
    public void ASecondOpenerIsRefused()
    {
        var log = LogPath();
        File.WriteAllLines(log, [Line(0, "An ice giant died.")]);
        using var first = LogCache.Open(CachePath(), log, emuMode: false);
        Assert.Throws<IOException>(() => LogCache.Open(CachePath(), log, emuMode: false));
    }

    // ---- sessions ----

    private static async Task<Session> RunToEndAsync(string log, LogCache? cache, bool checkpoint = true)
    {
        var session = new Session(log, ingestOptions: new IngestOptions { Follow = false }, cache: cache);
        await session.RunAsync(CancellationToken.None);
        if (checkpoint)
        {
            session.Checkpoint();
        }

        return session;
    }

    private static void AssertSameState(Session expected, Session actual)
    {
        Assert.Equal(expected.Records.Count, actual.Records.Count);
        for (var i = 0; i < expected.Records.Count; i++)
        {
            Assert.Equal(expected.Records[i].Timestamp, actual.Records[i].Timestamp);
            Assert.Equal(expected.Records[i].Event, actual.Records[i].Event);
        }

        Assert.Equal(expected.Fights.Fights.Count, actual.Fights.Fights.Count);
        for (var i = 0; i < expected.Fights.Fights.Count; i++)
        {
            var e = expected.Fights.Fights[i];
            var a = actual.Fights.Fights[i];
            Assert.Equal(e.Name, a.Name);
            Assert.Equal(e.BeginTime, a.BeginTime);
            Assert.Equal(e.LastDamageTime, a.LastDamageTime);
            Assert.Equal(e.Dead, a.Dead);
            Assert.Equal(e.DamageTotal, a.DamageTotal);
            Assert.Equal(e.TankingTotal, a.TankingTotal);
            Assert.Equal(e.DamageByActor.Keys.Order(), a.DamageByActor.Keys.Order());
        }

        Assert.Equal(expected.UnrecognizedLines, actual.UnrecognizedLines);
        Assert.Equal(expected.ParserFailures, actual.ParserFailures);
        Assert.Equal(expected.MalformedLines, actual.MalformedLines);
        Assert.Equal(expected.StanceSwitches, actual.StanceSwitches);
    }

    [Fact]
    public async Task AResumedSessionMatchesAColdOneAfterTheLogGrows()
    {
        var log = LogPath();
        var generator = new SyntheticLogGenerator(seed: 7, playerCount: 12);
        generator.WriteFile(log, 2 << 20);
        // A few shapes the generator does not emit, so the resumed counters
        // and the identity path are exercised too.
        File.AppendAllLines(log,
        [
            "this line has no timestamp",
            Line(100_000, "Kizant tells the raid, 'pulling'"),
            Line(100_001, "Xobatik says 'My leader is Raider02'"),
            Line(100_002, "You assume a defensive stance."),
            Line(100_003, "some words the parser has never seen"),
        ]);

        // First open: cold, and it leaves a checkpoint behind.
        using (var cold = await RunToEndAsync(log, LogCache.Open(CachePath(), log, false)))
        {
            Assert.Equal(0, cold.RestoredRecords);
            Assert.True(cold.Records.Count > 10_000);
        }

        // The game keeps writing.
        var more = new SyntheticLogGenerator(seed: 8, playerCount: 12, start: T0.AddDays(1)).Lines(TimeSpan.FromMinutes(5)).ToList();
        File.AppendAllLines(log, more);
        File.AppendAllLines(log, [Line(200_000, "You assume an evasive fighting style.")]);

        using var fresh = await RunToEndAsync(log, null);
        using (var resumed = await RunToEndAsync(log, LogCache.Open(CachePath(), log, false)))
        {
            Assert.True(resumed.RestoredRecords > 10_000);
            Assert.True(resumed.Records.Count > resumed.RestoredRecords);
            AssertSameState(fresh, resumed);
            Assert.Equal(1, fresh.MalformedLines);
            Assert.Equal(2, fresh.StanceSwitches);
            Assert.True(fresh.UnrecognizedLines >= 1);
        }

        // And the second checkpoint covers the tail, so a third open restores
        // everything. (Each session holds the cache file while it lives.)
        using var third = await RunToEndAsync(log, LogCache.Open(CachePath(), log, false), checkpoint: false);
        Assert.Equal(fresh.Records.Count, third.RestoredRecords);
        AssertSameState(fresh, third);
    }

    [Fact]
    public async Task ACorruptCacheFallsBackToTheParser()
    {
        var log = LogPath();
        new SyntheticLogGenerator(seed: 3, playerCount: 8).WriteFile(log, 256 << 10);
        using (await RunToEndAsync(log, LogCache.Open(CachePath(), log, false)))
        {
        }

        using (var f = new FileStream(CachePath(), FileMode.Open, FileAccess.ReadWrite))
        {
            f.Seek(4096 + 8, SeekOrigin.Begin);
            f.Write(new byte[64]);
        }

        using var fresh = await RunToEndAsync(log, null);
        using (var resumed = await RunToEndAsync(log, LogCache.Open(CachePath(), log, false)))
        {
            Assert.Equal(0, resumed.RestoredRecords);
            AssertSameState(fresh, resumed);
        }

        // The fallback also rewrote the cache, so the next open is warm again.
        using var again = await RunToEndAsync(log, LogCache.Open(CachePath(), log, false), checkpoint: false);
        Assert.Equal(fresh.Records.Count, again.RestoredRecords);
    }

    [Fact]
    public async Task AWindowedSessionRestoresOnlyTheWindowAndDoesNotCheckpoint()
    {
        var log = LogPath();
        File.WriteAllLines(log, Enumerable.Range(0, 100).Select(i => Line(i * 10, $"Raider01 crushes an ice giant for {i + 1} points of damage.")));
        using (await RunToEndAsync(log, LogCache.Open(CachePath(), log, false)))
        {
        }

        var from = T0.AddSeconds(500);
        using var cache = LogCache.Open(CachePath(), log, false);
        using var windowed = new Session(log, ingestOptions: new IngestOptions { Follow = false, BackfillFrom = from }, cache: cache);
        await windowed.RunAsync(CancellationToken.None);
        Assert.Equal(100, windowed.RestoredRecords);
        Assert.Equal(50, windowed.Records.Count);
        Assert.All(windowed.Records.Range(DateTime.MinValue, DateTime.MaxValue), r => Assert.True(r.Timestamp >= from));

        windowed.Checkpoint();
        Assert.Equal(100, cache.Checkpoint!.RecordCount); // untouched
    }

    [Fact]
    public async Task ATruncatedLogMidSessionRestartsTheCacheFromTheNewContent()
    {
        var log = LogPath();
        File.WriteAllLines(log, Enumerable.Range(0, 20).Select(i => Line(i, $"Raider01 crushes an ice giant for {i + 1} points of damage.")));

        using var cache = LogCache.Open(CachePath(), log, false);
        using var session = new Session(log, ingestOptions: new IngestOptions(), clock: new SpinClock(), cache: cache);
        using var cancel = new CancellationTokenSource();
        var backfilled = new TaskCompletionSource();
        var seenLive = new TaskCompletionSource();
        session.BatchProcessed += b =>
        {
            if (b.Phase == IngestPhase.BackfillComplete)
            {
                backfilled.TrySetResult();
            }

            if (b.Phase == IngestPhase.Live && b.Generation > 0 && b.Entries.Count > 0)
            {
                seenLive.TrySetResult();
            }
        };
        var run = Task.Run(() => session.RunAsync(cancel.Token));
        await backfilled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Checkpoint();
        Assert.Equal(20, cache.Checkpoint!.RecordCount);

        // /log clear, then new lines.
        using (new FileStream(log, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
        }

        using (var stream = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        using (var writer = new StreamWriter(stream, Encoding.Latin1))
        {
            writer.Write(Line(1000, "A shadow drake died.") + "\r\n");
            writer.Write(Line(1001, "Raider02 kicks a shadow drake for 5 points of damage.") + "\r\n");
        }

        await seenLive.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Checkpoint();
        cancel.Cancel();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
        }

        // The cache now describes only the new file: two records, from its top.
        Assert.Equal(2, cache.Checkpoint!.RecordCount);
        cache.Dispose();

        using var reopened = await RunToEndAsync(log, LogCache.Open(CachePath(), log, false), checkpoint: false);
        using var fresh = await RunToEndAsync(log, null);
        Assert.Equal(2, reopened.RestoredRecords);
        AssertSameState(fresh, reopened);
    }

    // ---- ingestion offsets ----

    /// <summary>
    /// Every alignment of chunk boundary against line boundary — a chunk
    /// ending mid-line, exactly on a newline, exactly after completing a
    /// carried line — must leave the resume offset on a line start. The
    /// buffer sizes are swept because the one that ends a chunk right after a
    /// completion is the one that once slipped through.
    /// </summary>
    [Theory]
    [InlineData(64)]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(127)]
    [InlineData(150)]
    [InlineData(777)]
    public async Task ResumeOffsetIsALineStartAndResumingThereLosesNothing(int bufferSize)
    {
        var log = LogPath();
        // Fixed-length lines, so a buffer size pins the boundary pattern
        // rather than leaving it to chance.
        var lines = Enumerable.Range(0, 200).Select(i => Line(i, $"Raider01 kicks a rat for {i:D5} pts.")).ToList();
        var lineBytes = lines[0].Length + 2; // + CRLF
        Assert.All(lines, l => Assert.Equal(lineBytes - 2, l.Length));
        File.WriteAllLines(log, lines);

        var ingestion = new LogFileIngestion(log, new IngestOptions { Follow = false, ReadBufferSize = bufferSize });
        var run = ingestion.RunAsync(CancellationToken.None);
        var batches = new List<LogBatch>();
        await foreach (var b in ingestion.Batches.ReadAllAsync())
        {
            batches.Add(b);
        }

        await run;

        var withEntries = batches.Where(b => b.Entries.Count > 0).ToList();
        Assert.True(withEntries.Count > 10);
        var bytes = File.ReadAllBytes(log);
        var all = withEntries.SelectMany(b => b.Entries).ToList();
        Assert.Equal(200, all.Count);
        var before = 0;
        foreach (var b in withEntries)
        {
            before += b.Entries.Count;
            Assert.True(b.ResumeOffset > 0 && b.ResumeOffset <= bytes.Length);
            Assert.True(b.ResumeOffset <= b.BytesProcessed);
            Assert.Equal((byte)'\n', bytes[b.ResumeOffset - 1]);
            Assert.Equal(before * (long)lineBytes, b.ResumeOffset);

            // Resuming at this batch's offset yields exactly the entries after
            // it — nothing lost, nothing doubled, nothing malformed.
            var resumed = new LogFileIngestion(log, new IngestOptions { Follow = false });
            var resumedRun = resumed.RunAsync(CancellationToken.None, b.ResumeOffset);
            var tail = new List<Parsing.LogEntry>();
            await foreach (var rb in resumed.Batches.ReadAllAsync())
            {
                tail.AddRange(rb.Entries);
            }

            await resumedRun;
            Assert.Equal(all.Skip(before), tail);
            Assert.Equal(0, resumed.MalformedLines);
        }

        // And the last batch of all ends at the end of the file.
        Assert.Equal(bytes.Length, batches[^1].ResumeOffset);
    }

    [Fact]
    public async Task ResumingPastTheEndOfTheLogFaults()
    {
        var log = LogPath();
        File.WriteAllLines(log, [Line(0, "An ice giant died.")]);
        var ingestion = new LogFileIngestion(log, new IngestOptions { Follow = false });
        await Assert.ThrowsAsync<IOException>(() => ingestion.RunAsync(CancellationToken.None, 10_000));
    }

    [Fact]
    public void PooledStringsAreShared()
    {
        var pool = new StringPool();
        var a = (DamageEvent)pool.Canonicalize(new DamageEvent(new string("Raider01"), new string("An ice giant"), 1, DamageKind.Melee, new string("Crushes")));
        var b = (DamageEvent)pool.Canonicalize(new DamageEvent(new string("Raider01"), new string("An ice giant"), 2, DamageKind.Melee, new string("Crushes")));
        Assert.Same(a.Attacker, b.Attacker);
        Assert.Same(a.Defender, b.Defender);
        Assert.Same(a.SubType, b.SubType);
        Assert.Equal(3, pool.Count);

        // Already-canonical events come back as themselves, no copy.
        Assert.Same(b, pool.Canonicalize(b));

        // Chat text is not pooled.
        var chat = (ChatEvent)pool.Canonicalize(new ChatEvent(ChatChannel.Say, "Raider01", "hello"));
        Assert.Equal(3, pool.Count);
        Assert.Same(a.Attacker, chat.Sender);
    }
}
