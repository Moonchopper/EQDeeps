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

    /// <summary>Answers from memory, counts what was asked, and can be told to fail or to 304.</summary>
    private sealed class FakeSource : IReferenceSource
    {
        public readonly List<string> Requested = [];
        public readonly Dictionary<string, string> Bodies = new(StringComparer.Ordinal);
        public string? Failure;
        public bool AlwaysNotModified;

        public string Name => "FakeBase";

        public string HomeUrl => "https://example.invalid";

        public string NpcUrl(int id) => $"https://example.invalid/npcs/{id}/";

        public Task<ReferenceFetch> GetAsync(string path, string? etag, CancellationToken ct)
        {
            Requested.Add(path);
            if (Failure is not null)
            {
                return Task.FromResult(ReferenceFetch.Failure(Failure));
            }

            if (AlwaysNotModified)
            {
                return Task.FromResult(ReferenceFetch.NotModified(etag));
            }

            return Task.FromResult(Bodies.TryGetValue(path, out var body)
                ? ReferenceFetch.Fetched(body, "\"etag-" + path.GetHashCode() + "\"")
                : ReferenceFetch.Failure("404"));
        }
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
}
