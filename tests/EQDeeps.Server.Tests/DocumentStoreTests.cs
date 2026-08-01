using System.Text.Json;
using EQDeeps.Server;
using Xunit;

namespace EQDeeps.Server.Tests;

public sealed class DocumentStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));
    private readonly DocumentStore _store;

    public DocumentStoreTests()
    {
        _store = new DocumentStore(_dir);
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

    [Fact]
    public void MissingDocumentReadsAsNull()
    {
        Assert.Null(_store.Read("dashboards"));
    }

    [Fact]
    public void DocumentsRoundTripVerbatim()
    {
        using var doc = JsonDocument.Parse("""{"dashboards":[{"id":"d1","name":"Raid night","panels":[]}]}""");
        _store.Write("dashboards", doc.RootElement);

        var back = _store.Read("dashboards");
        Assert.NotNull(back);
        Assert.Equal("Raid night", back.Value.GetProperty("dashboards")[0].GetProperty("name").GetString());

        // Overwrite wins.
        using var doc2 = JsonDocument.Parse("""{"dashboards":[]}""");
        _store.Write("dashboards", doc2.RootElement);
        Assert.Equal(0, _store.Read("dashboards")!.Value.GetProperty("dashboards").GetArrayLength());
    }

    [Fact]
    public void CorruptFilesReadAsAbsent()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "dashboards.json"), "{not json");
        Assert.Null(_store.Read("dashboards"));
    }

    [Fact]
    public void UnknownKeysAreRejected()
    {
        Assert.False(DocumentStore.IsValidKey("../../etc/passwd"));
        Assert.Throws<ArgumentException>(() => _store.Read("nope"));
    }
}
