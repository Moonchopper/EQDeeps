using EQDeeps.Server;
using Xunit;

namespace EQDeeps.Server.Tests;

public sealed class SampleLogTests : IDisposable
{
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

    [Fact]
    public void ExtractsEmbeddedLogAndIsIdempotent()
    {
        var sample = new SampleLog(_dir);
        var path = sample.TryEnsureExtracted();
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(SampleLog.FileName, Path.GetFileName(path));

        // Real log content: timestamped lines, the demo character's name baked in.
        using (var reader = new StreamReader(path!))
        {
            var first = reader.ReadLine();
            Assert.StartsWith("[", first);
        }

        // A second call is a no-op (stamp matches) — same path, no rewrite.
        var written = File.GetLastWriteTimeUtc(path!);
        Assert.Equal(path, sample.TryEnsureExtracted());
        Assert.Equal(written, File.GetLastWriteTimeUtc(path!));

        // A deleted file is re-extracted.
        File.Delete(path!);
        Assert.Equal(path, sample.TryEnsureExtracted());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void IsSamplePathMatchesOnlyTheExtractedFile()
    {
        var sample = new SampleLog(_dir);
        Assert.True(sample.IsSamplePath(sample.FilePath));
        Assert.True(sample.IsSamplePath(sample.FilePath.ToUpperInvariant()));
        Assert.False(sample.IsSamplePath(@"C:\logs\eqlog_Kizant_xegony.txt"));
        Assert.False(sample.IsSamplePath("\0not a path"));
    }

    [Fact]
    public void SampleFileNameParsesAsSampleAtDemo()
    {
        var sample = new SampleLog(_dir);
        var path = sample.TryEnsureExtracted();
        Assert.NotNull(path);
        var described = LogDiscovery.Describe(path!, "sample");
        Assert.NotNull(described);
        Assert.Equal("Sample", described!.Character);
        Assert.Equal("demo", described.Server);
        Assert.Equal("sample", described.Source);
    }
}
