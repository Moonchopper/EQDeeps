using System.Text.Json;
using EQDeeps.Server.Reference;
using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// The reference cache (F30, ADR-020) — what it fetches, what it keeps, and
/// what it does when the other end is unhelpful.
///
/// <para>Every test here uses a fake source. That is the point: a feature that
/// reaches a third party has to be provable without one, and CI must never
/// depend on somebody's website being up.</para>
/// </summary>
public sealed class NpcReferenceStoreTests : IDisposable
{
    private const string Index = """
        [["Fippy Darkpaw (5)","n",2119],["a rabid kobold (6)","n",1201],["a rabid kobold (9)","n",1202]]
        """;

    private const string Shard = """
        {"2119":{"id":2119,"name":"Fippy Darkpaw","level":5,"hp":75,"race":"Gnoll"}}
        """;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));

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

    /// <summary>
    /// Answers from memory, counts what was asked, and can be told to fail (everywhere, or on one
    /// path only) or to always 304. Also tracks the etag each request carried and how many requests
    /// were ever in flight at once, for the atlas walk's own tests (C1): a real <c>await
    /// Task.Delay</c> gap between recording "in flight" and answering means the walk's "never two
    /// shards at once" claim (Gotcha 2) is actually exercised, not trivially true because this fake
    /// never yielded.
    /// </summary>
    private sealed class FakeSource : IReferenceSource
    {
        public readonly List<string> Requested = [];
        public readonly Dictionary<string, string> Bodies = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string?> LastEtag = new(StringComparer.Ordinal);
        public readonly HashSet<string> FailPaths = [];
        public string? Failure;
        public bool AlwaysNotModified;
        private int _inFlight;
        public int MaxConcurrent;

        public string Name => "FakeBase";

        public string HomeUrl => "https://example.invalid";

        public string NpcUrl(int id) => $"https://example.invalid/npcs/{id}/";

        public async Task<ReferenceFetch> GetAsync(string path, string? etag, CancellationToken ct)
        {
            Requested.Add(path);
            LastEtag[path] = etag;
            var now = Interlocked.Increment(ref _inFlight);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            try
            {
                await Task.Delay(2, ct).ConfigureAwait(false);

                if (Failure is not null || FailPaths.Contains(path))
                {
                    return ReferenceFetch.Failure(Failure ?? "fake failure: " + path);
                }

                if (AlwaysNotModified)
                {
                    return ReferenceFetch.NotModified(etag);
                }

                return Bodies.TryGetValue(path, out var body)
                    ? ReferenceFetch.Fetched(body, "\"etag-" + path.GetHashCode() + "\"")
                    : ReferenceFetch.NotFound();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>
    /// A shipped snapshot (ADR-020 Decision 1's amendment): a fixed <see cref="SnapshotUtc"/> plus
    /// whatever files a test puts in <see cref="Files"/>/<see cref="Etags"/>. Never touches the
    /// network — that is the whole point of the interface — so pairing it with a <see cref="FakeSource"/>
    /// whose <see cref="FakeSource.Requested"/> stays empty is how S1-S4 prove the store's read paths
    /// never reach it.
    /// </summary>
    private sealed class FakeSnapshot : IReferenceSnapshot
    {
        public DateTime? SnapshotUtc { get; set; } = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        public readonly Dictionary<string, string> Files = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Etags = new(StringComparer.Ordinal);

        public string? Read(string fileName) => Files.TryGetValue(fileName, out var text) ? text : null;

        public string? EtagFor(string fileName) => Etags.TryGetValue(fileName, out var etag) ? etag : null;
    }

    /// <summary>
    /// The shard at Neriak Third Gate's client id (42): two of its rows stand
    /// there, one is filed under it but claims another zone — which the roster
    /// must not swallow just because the address matched.
    /// </summary>
    private const string NeriakShard = """
        {"42000":{"id":42000,"name":"a ghoul","level":13,"maxLevel":13,"hp":299,
          "zones":[{"zone":"neriakc","longName":"Neriak - 3rd Gate","spawnPoints":2,"locs":[[-1312,666,-101.7],[-1300,660,-101]]}]},
         "42001":{"id":42001,"name":"Cleric of Innoruuk","level":50,
          "zones":[{"zone":"neriakc","longName":"Neriak - 3rd Gate","spawnPoints":1,"locs":[[-100,50,0]]}]},
         "42002":{"id":42002,"name":"a lost soul","level":9,
          "zones":[{"zone":"somewhereelse","longName":"Somewhere Else","spawnPoints":1,"locs":[[0,0,0]]}]}}
        """;

    [Fact]
    public async Task ARosterIsTheShardAtTheZonesId_KeptOnlyWhereItSaysItStandsThere()
    {
        var source = Source();
        source.Bodies["/data/npcs/42.json"] = NeriakShard;
        var store = new NpcReferenceStore(source, _dir);

        var roster = await store.RosterAsync("neriakc");
        Assert.True(roster.Known);
        Assert.Equal("Neriak - 3rd Gate", roster.ZoneName);
        Assert.Equal(["a ghoul", "Cleric of Innoruuk"], roster.Npcs.Select(n => n.Name));
        Assert.Equal(2, roster.Npcs[0].SpawnPoints);
        Assert.Equal([-1312, 666, -101.7], roster.Npcs[0].Locations[0]);
        Assert.Equal(["/data/npcs/42.json"], source.Requested);

        // A zone whose shard the site does not have: not known, and not an
        // error either — the site is up, it just lists nothing there. Asked
        // twice, fetched once.
        var missing = await store.RosterAsync("poknowledge");
        Assert.False(missing.Known);
        Assert.Empty(missing.Npcs);
        Assert.Null(store.Status().Error);
        await store.RosterAsync("poknowledge");
        Assert.Equal(2, source.Requested.Count);

        // A short name the table has no row for cannot be addressed at all.
        Assert.False((await store.RosterAsync("nosuchzone")).Known);
        Assert.Equal(2, source.Requested.Count);
    }

    /// <summary>Back-dates a cached file, so the once-a-day revalidation is due.</summary>
    private static void Age(string path, TimeSpan by) =>
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - by);

    private FakeSource Source() => new()
    {
        Bodies =
        {
            ["/data/search-index.json"] = Index,
            ["/data/npcs/2.json"] = Shard,
        },
    };

    [Fact]
    public async Task FetchesOnceThenServesFromDiskInTheNextSession()
    {
        var first = Source();
        var store = new NpcReferenceStore(first, _dir);

        var index = await store.IndexAsync();
        Assert.NotNull(index);
        Assert.Equal(2, index!.NameCount);
        Assert.Equal(75, (await store.DetailAsync(2119))!.Hp);
        Assert.Equal(["/data/search-index.json", "/data/npcs/2.json"], first.Requested);

        // Same process, second ask: nothing more goes out.
        await store.IndexAsync();
        await store.DetailAsync(2119);
        Assert.Equal(2, first.Requested.Count);

        // A new run reads what the last one wrote — and, the copy being
        // hours rather than days old, says nothing to anyone at all.
        var second = Source();
        var reopened = new NpcReferenceStore(second, _dir);
        Assert.Equal(2, (await reopened.IndexAsync())!.NameCount);
        Assert.Equal("Fippy Darkpaw", (await reopened.DetailAsync(2119))!.Name);
        Assert.Empty(second.Requested);

        // Once it is a day old, one conditional GET — which a 304 answers for
        // nothing, leaving the cache in place and still no shard fetched.
        Age(Path.Combine(_dir, "reference", "search-index.json"), TimeSpan.FromDays(2));
        var third = Source();
        third.AlwaysNotModified = true;
        var later = new NpcReferenceStore(third, _dir);
        Assert.Equal(2, (await later.IndexAsync())!.NameCount);
        Assert.Equal("Fippy Darkpaw", (await later.DetailAsync(2119))!.Name);
        Assert.Equal(["/data/search-index.json"], third.Requested);
    }

    [Fact]
    public async Task AFailedFetchIsReportedAndCostsNothingElse()
    {
        var source = Source();
        source.Failure = "no network";
        var store = new NpcReferenceStore(source, _dir);

        Assert.Null(await store.IndexAsync());
        Assert.Null(await store.DetailAsync(2119));

        var status = store.Status();
        Assert.False(status.Available);
        Assert.Equal("no network", status.Error);
        Assert.Equal("FakeBase", status.Source);

        // Nothing was written, so a later run starts clean rather than caching a failure.
        Assert.False(File.Exists(Path.Combine(_dir, "reference", "search-index.json")));
    }

    [Fact]
    public async Task SwitchedOffMeansNothingLeavesTheMachine()
    {
        var source = Source();
        var store = new NpcReferenceStore(source, _dir, enabled: false);

        Assert.Null(await store.IndexAsync());
        Assert.Null(await store.DetailAsync(2119));
        Assert.Empty(source.Requested);
        Assert.Contains("switched off", store.Status().Error);
    }

    [Fact]
    public async Task AnIndexThatParsesToNothingDoesNotReplaceAGoodOne()
    {
        var good = Source();
        var store = new NpcReferenceStore(good, _dir);
        Assert.Equal(2, (await store.IndexAsync())!.NameCount);

        // Their shape moves under us, on a copy old enough to be rechecked:
        // the cached index stands, and the status says why it is not moving.
        Age(Path.Combine(_dir, "reference", "search-index.json"), TimeSpan.FromDays(2));
        var broken = Source();
        broken.Bodies["/data/search-index.json"] = "{\"npcs\":\"moved\"}";
        var reopened = new NpcReferenceStore(broken, _dir);
        Assert.Equal(2, (await reopened.IndexAsync())!.NameCount);
        Assert.Equal("the index could not be read", reopened.Status().Error);
    }

    [Fact]
    public async Task TheCacheLivesUnderTheRedirectedRootAndCanBeDeleted()
    {
        var store = new NpcReferenceStore(Source(), _dir);
        await store.IndexAsync();
        await store.DetailAsync(2119);

        var folder = Path.Combine(_dir, "reference");
        Assert.True(File.Exists(Path.Combine(folder, "search-index.json")));
        Assert.True(File.Exists(Path.Combine(folder, "npcs-2.json")));
        Assert.True(File.Exists(Path.Combine(folder, "etags.json")));

        // Deleting it is always safe: a fresh store just asks again.
        Directory.Delete(folder, recursive: true);
        var again = Source();
        Assert.NotNull(await new NpcReferenceStore(again, _dir).IndexAsync());
        Assert.Contains("/data/search-index.json", again.Requested);
    }

    // ---- the atlas walk (F35, ADR-023 Decision 7) --------------------------

    [Fact]
    public async Task TheAtlasWalksEveryShardOnceInSequenceAndSkipsAFailingOne()
    {
        var source = new FakeSource
        {
            Bodies =
            {
                ["/data/search-index.json"] = """
                    [["a gnoll (5)","n",1001],["a kobold (6)","n",2001],["a bat (3)","n",3001]]
                    """,
                ["/data/npcs/1.json"] = """
                    {"1001":{"id":1001,"name":"a gnoll","level":5,"race":"Gnoll","respawn":300,
                      "zones":[{"zone":"blackburrow","longName":"Blackburrow","spawnPoints":4,"locs":[[0,0,0]]}]}}
                    """,
                ["/data/npcs/3.json"] = """
                    {"3001":{"id":3001,"name":"a bat","level":3,"race":"Bat","respawn":300,
                      "zones":[{"zone":"kithicor","longName":"Kithicor Forest","spawnPoints":2,"locs":[[0,0,0]]}]}}
                    """,
            },
        };
        source.FailPaths.Add("/data/npcs/2.json"); // shard 2 (the kobold) never answers

        var store = new NpcReferenceStore(source, _dir, atlasPause: TimeSpan.Zero);

        // _atlasStarted flips synchronously inside StartAtlas, before Task.Run's work has had any
        // chance to run on the thread pool, so the very next line reliably observes the walk mid
        // flight — "status goes Running" is not just narrative.
        var justStarted = store.StartAtlas();
        Assert.True(justStarted.Running);
        Assert.False(justStarted.Complete);

        // A second call back to back: the compare-and-swap guard (not timing) is what proves it
        // starts nothing (Gotcha 2), so this needs no race to be meaningful.
        store.StartAtlas();
        await (store.AtlasTask ?? Task.CompletedTask);

        var status = store.AtlasStatus();
        Assert.True(status.Enabled);
        Assert.True(status.Complete);
        Assert.False(status.Running);
        Assert.Equal(3, status.ZonesTotal);
        Assert.Equal(3, status.ZonesRead);
        Assert.NotNull(status.Error);

        // Every shard requested exactly once, in ascending order — a second StartAtlas call left
        // no trace, and the index was read only once too.
        Assert.Equal(
            ["/data/search-index.json", "/data/npcs/1.json", "/data/npcs/2.json", "/data/npcs/3.json"],
            source.Requested);
        Assert.Equal(1, source.MaxConcurrent);

        // The failing shard contributed nothing, but the other two still made it into the atlas.
        var atlas = await store.AtlasAsync();
        Assert.Equal(2, atlas.Zones.Count);
        Assert.Contains(atlas.Zones, z => z.ShortName == "blackburrow");
        Assert.Contains(atlas.Zones, z => z.ShortName == "kithicor");
    }

    [Fact]
    public void ADisabledStoresAtlasWalkMakesNoRequests()
    {
        var source = Source();
        var store = new NpcReferenceStore(source, _dir, enabled: false, atlasPause: TimeSpan.Zero);

        var status = store.StartAtlas();

        Assert.False(status.Enabled);
        Assert.False(status.Running);
        Assert.False(status.Complete);
        Assert.Empty(source.Requested);
    }

    // ---- shard revalidation (F35, ADR-023 Decision 7) -----------------------

    [Fact]
    public async Task AFreshShardCostsNothing_AStaleOneRevalidatesOnItsEtag_AndAFailureKeepsWhatItHad()
    {
        var first = Source();
        var store = new NpcReferenceStore(first, _dir);
        Assert.Equal(75, (await store.DetailAsync(2119))!.Hp);

        var shardPath = Path.Combine(_dir, "reference", "npcs-2.json");
        Assert.True(File.Exists(shardPath));

        // Fresh (just written): a brand-new store instance still asks nothing.
        var second = Source();
        var reopened = new NpcReferenceStore(second, _dir);
        Assert.Equal(75, (await reopened.DetailAsync(2119))!.Hp);
        Assert.Empty(second.Requested);

        // Aged past ShardMaxAge (7 days): exactly one conditional GET, carrying the ETag the first
        // fetch stored — read back from etags.json itself, not assumed (Gotcha 3).
        Age(shardPath, TimeSpan.FromDays(8));
        var storedEtag = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(_dir, "reference", "etags.json")))!["npcs-2.json"];

        var third = Source();
        third.AlwaysNotModified = true;
        var stale = new NpcReferenceStore(third, _dir);
        var beforeTouch = File.GetLastWriteTimeUtc(shardPath);
        Assert.Equal(75, (await stale.DetailAsync(2119))!.Hp);
        Assert.Equal(["/data/npcs/2.json"], third.Requested);
        Assert.Equal(storedEtag, third.LastEtag["/data/npcs/2.json"]);

        // A 304 keeps the cached data and freshens the write time.
        Assert.True(File.GetLastWriteTimeUtc(shardPath) > beforeTouch);

        // Aged again, and this time the revalidation fails outright: the cached copy still answers.
        Age(shardPath, TimeSpan.FromDays(8));
        var fourth = Source();
        fourth.Failure = "no network";
        var failing = new NpcReferenceStore(fourth, _dir);
        Assert.Equal(75, (await failing.DetailAsync(2119))!.Hp);
        Assert.Equal(["/data/npcs/2.json"], fourth.Requested);
    }

    // ---- the shipped snapshot (ADR-020 Decision 1's amendment) --------------

    /// <summary>Zone 42's rows, with the race and respawn <c>SlayerAtlas</c> needs to count them — <see cref="NeriakShard"/> itself has neither, and other tests depend on that.</summary>
    private const string NeriakShardWithRace = """
        {"42000":{"id":42000,"name":"a ghoul","level":13,"maxLevel":13,"hp":299,"race":"Ghoul","respawn":300,
          "zones":[{"zone":"neriakc","longName":"Neriak - 3rd Gate","spawnPoints":2,"locs":[[-1312,666,-101.7],[-1300,660,-101]]}]},
         "42001":{"id":42001,"name":"Cleric of Innoruuk","level":50,"race":"Human","respawn":600,
          "zones":[{"zone":"neriakc","longName":"Neriak - 3rd Gate","spawnPoints":1,"locs":[[-100,50,0]]}]}}
        """;

    private FakeSnapshot SnapshotWithFippyAndNeriak() => new()
    {
        Files =
        {
            ["search-index.json"] = """
                [["Fippy Darkpaw (5)","n",2119],["a ghoul (13)","n",42000],["Cleric of Innoruuk (50)","n",42001]]
                """,
            ["npcs-2.json"] = Shard,
            ["npcs-42.json"] = NeriakShardWithRace,
        },
    };

    [Fact]
    public async Task S1_WithASnapshotEveryReadAnswersFromItAndTouchesNoNetwork()
    {
        var source = new FakeSource(); // no bodies configured at all: any request would be a 404
        var snapshot = SnapshotWithFippyAndNeriak();
        var store = new NpcReferenceStore(source, _dir, atlasPause: TimeSpan.Zero, snapshot: snapshot);

        var index = await store.IndexAsync();
        Assert.Equal(3, index!.EntryCount);

        var detail = await store.DetailAsync(2119);
        Assert.Equal("Fippy Darkpaw", detail!.Name);
        Assert.Equal(75, detail.Hp);

        var roster = await store.RosterAsync("neriakc");
        Assert.True(roster.Known);
        Assert.Equal(["a ghoul", "Cleric of Innoruuk"], roster.Npcs.Select(n => n.Name));

        var justStarted = store.StartAtlas();
        Assert.True(justStarted.Running);
        await (store.AtlasTask ?? Task.CompletedTask);
        var atlasStatus = store.AtlasStatus();
        Assert.True(atlasStatus.Complete);
        Assert.Equal(2, atlasStatus.ZonesTotal); // shard 2 (Fippy) and shard 42 (Neriak)

        var atlas = await store.AtlasAsync();
        Assert.Contains(atlas.Zones, z => z.ShortName == "neriakc"); // Fippy carries no zones/race at all: contributes nothing

        Assert.Empty(source.Requested);
    }

    [Fact]
    public async Task S2_ACacheFileNewerThanTheSnapshotWins_OlderOrEqualIsIgnoredNotDeleted()
    {
        var snapshot = SnapshotWithFippyAndNeriak();
        var snapshotUtc = snapshot.SnapshotUtc!.Value;

        var referenceDir = Path.Combine(_dir, "reference");
        Directory.CreateDirectory(referenceDir);
        var shardPath = Path.Combine(referenceDir, "npcs-2.json");
        const string NewerShard = """
            {"2119":{"id":2119,"name":"Fippy Darkpaw","level":5,"hp":999,"race":"Gnoll"}}
            """;
        var source = new FakeSource();

        // Strictly after the snapshot was taken: a Refresh could only have put it there, so it wins.
        File.WriteAllText(shardPath, NewerShard);
        File.SetLastWriteTimeUtc(shardPath, snapshotUtc.AddDays(1));
        Assert.Equal(999, (await new NpcReferenceStore(source, _dir, snapshot: snapshot).DetailAsync(2119))!.Hp);

        // Exactly at the snapshot's own moment: not newer, so ignored — and, per S2, not deleted either.
        File.SetLastWriteTimeUtc(shardPath, snapshotUtc);
        Assert.Equal(75, (await new NpcReferenceStore(source, _dir, snapshot: snapshot).DetailAsync(2119))!.Hp);
        Assert.True(File.Exists(shardPath));

        // Older than the snapshot: also ignored.
        File.SetLastWriteTimeUtc(shardPath, snapshotUtc.AddDays(-1));
        Assert.Equal(75, (await new NpcReferenceStore(source, _dir, snapshot: snapshot).DetailAsync(2119))!.Hp);

        Assert.Empty(source.Requested);
    }

    // S3 (no snapshot: every pre-existing test in this file passes untouched) needs no new test of
    // its own — it is exactly the rest of this file, run with the default (null) snapshot.

    [Fact]
    public async Task S4_StartRefreshWalksTheIndexThenEachShardOnce_ConditionalOnTheRightEtag()
    {
        var snapshot = new FakeSnapshot
        {
            Files =
            {
                ["search-index.json"] = """
                    [["a gnoll (5)","n",1001]]
                    """,
                ["npcs-1.json"] = """
                    {"1001":{"id":1001,"name":"a gnoll","level":5,"hp":50,"race":"Gnoll","respawn":300,
                      "zones":[{"zone":"blackburrow","longName":"Blackburrow","spawnPoints":4,"locs":[[0,0,0]]}]}}
                    """,
            },
            Etags =
            {
                ["search-index.json"] = "snap-index-etag",
                ["npcs-1.json"] = "snap-shard1-etag",
            },
        };

        // The refresh fetch reveals a bigger world than the snapshot: the same gnoll shard, updated
        // (hp moved), a kobold shard that will fail, and a bat shard nothing has ever loaded before —
        // the "a mob that exists only in the new shard" case.
        var source = new FakeSource
        {
            Bodies =
            {
                ["/data/search-index.json"] = """
                    [["a gnoll (5)","n",1001],["a kobold (6)","n",2001],["a bat (3)","n",3001]]
                    """,
                ["/data/npcs/1.json"] = """
                    {"1001":{"id":1001,"name":"a gnoll","level":5,"hp":80,"race":"Gnoll","respawn":300,
                      "zones":[{"zone":"blackburrow","longName":"Blackburrow","spawnPoints":4,"locs":[[0,0,0]]}]}}
                    """,
                ["/data/npcs/3.json"] = """
                    {"3001":{"id":3001,"name":"a bat","level":3,"race":"Bat","respawn":300,
                      "zones":[{"zone":"kithicor","longName":"Kithicor Forest","spawnPoints":2,"locs":[[0,0,0]]}]}}
                    """,
            },
        };
        source.FailPaths.Add("/data/npcs/2.json");

        var store = new NpcReferenceStore(source, _dir, atlasPause: TimeSpan.Zero, snapshot: snapshot);

        var justStarted = store.StartRefresh();
        Assert.True(justStarted.Refresh.Running);

        // Back to back: the CAS guard, not timing, is what proves this starts nothing extra.
        store.StartRefresh();
        await (store.RefreshTask ?? Task.CompletedTask);

        var status = store.Status();
        Assert.False(status.Refresh.Running);
        Assert.NotNull(status.Refresh.FinishedUtc);
        Assert.Equal(4, status.Refresh.FilesTotal); // index + shards 1, 2, 3
        Assert.Equal(4, status.Refresh.FilesChecked);
        Assert.Equal(3, status.Refresh.FilesChanged); // index, shard 1, shard 3 — shard 2 failed
        Assert.NotNull(status.Refresh.Error); // the kobold shard's failure, still visible after the rest ran

        // Never two requests in flight — the same guarantee the atlas walk gives (Gotcha 2).
        Assert.Equal(1, source.MaxConcurrent);

        // The ETag carried on each request: the snapshot's own for a file never yet cached, since
        // there was nothing on disk for any of these before this Refresh ran.
        Assert.Equal("snap-index-etag", source.LastEtag["/data/search-index.json"]);
        Assert.Equal("snap-shard1-etag", source.LastEtag["/data/npcs/1.json"]);
        Assert.Null(source.LastEtag["/data/npcs/2.json"]); // shard 2 was never in the snapshot at all
        Assert.Null(source.LastEtag["/data/npcs/3.json"]); // neither was shard 3

        // 200s wrote the cache and swapped memory: a fresh DetailAsync sees the new hp with zero
        // further requests, and the bat — only ever named by the refreshed index — is in the atlas.
        Assert.Equal(80, (await store.DetailAsync(1001))!.Hp);
        var atlas = await store.AtlasAsync();
        Assert.Contains(atlas.Zones, z => z.ShortName == "kithicor");
        Assert.Equal(4, source.Requested.Count); // no further requests from the reads just above

        // A later Refresh is a fresh one, not "already running" — pressing the button again works.
        var again = store.StartRefresh();
        Assert.True(again.Refresh.Running);
        await (store.RefreshTask ?? Task.CompletedTask);
        Assert.True(source.Requested.Count > 4);
    }

    /// <summary>
    /// A 200 whose body is the copy already in use is not a change. The site's validator says
    /// 200 for files that have not moved (measured 2026-09-22: eleven of eighty, one of them even
    /// when sent its own current ETag), so a Refresh that trusted the status code would rewrite
    /// them, stamp the cache "refreshed just now", and report changes that did not happen. The
    /// index is compared on its parsed rows, because the bundled copy is trimmed to the rows the
    /// app reads and the site's copy is whole.
    /// </summary>
    [Fact]
    public async Task S4b_ARefreshThatGets200sWithUnchangedContentReportsNoChangeAndWritesNothing()
    {
        const string shard = """
            {"1001":{"id":1001,"name":"a gnoll","level":5,"hp":50,"race":"Gnoll","respawn":300,
              "zones":[{"zone":"blackburrow","longName":"Blackburrow","spawnPoints":4,"locs":[[0,0,0]]}]}}
            """;
        var snapshot = new FakeSnapshot
        {
            Files =
            {
                // Trimmed, as the snapshot script writes it: the "n" row only.
                ["search-index.json"] = """[["a gnoll (5)","n",1001]]""",
                ["npcs-1.json"] = shard,
            },
        };
        var source = new FakeSource
        {
            Bodies =
            {
                // The site's whole index: the same "n" row plus an item row the app never reads.
                ["/data/search-index.json"] = """[["a gnoll (5)","n",1001],["Rusty Pick","i",5040]]""",
                ["/data/npcs/1.json"] = shard,
            },
        };

        var store = new NpcReferenceStore(source, _dir, atlasPause: TimeSpan.Zero, snapshot: snapshot);
        store.StartRefresh();
        await (store.RefreshTask ?? Task.CompletedTask);

        var status = store.Status();
        Assert.Equal(2, status.Refresh.FilesChecked);
        Assert.Equal(0, status.Refresh.FilesChanged);
        Assert.Null(status.Refresh.Error);
        Assert.Null(status.RefreshedUtc); // nothing was written, so nothing is "refreshed"
        Assert.False(File.Exists(Path.Combine(_dir, "reference", "npcs-1.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "reference", "search-index.json")));
    }

    [Fact]
    public void S4_ADisabledStoresRefreshMakesNoRequests()
    {
        var source = Source();
        var store = new NpcReferenceStore(source, _dir, enabled: false, atlasPause: TimeSpan.Zero);

        var status = store.StartRefresh();

        Assert.False(status.Refresh.Running);
        Assert.Empty(source.Requested);
    }
}
