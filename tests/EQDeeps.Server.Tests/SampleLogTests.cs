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
    public void ExtractsExactlyOneFileAndIsIdempotent()
    {
        var sample = new SampleLog(_dir);
        var path = sample.TryEnsureExtracted();
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(SampleLog.FileName, Path.GetFileName(path));

        // Real log content: timestamped lines.
        using (var reader = new StreamReader(path!))
        {
            var first = reader.ReadLine();
            Assert.StartsWith("[", first);
        }

        // A second call is a no-op (extracted length matches the gzip trailer) —
        // same path, no rewrite.
        var written = File.GetLastWriteTimeUtc(path!);
        Assert.Equal(path, sample.TryEnsureExtracted());
        Assert.Equal(written, File.GetLastWriteTimeUtc(path!));

        // A deleted or stale file is re-extracted, and leftovers from older
        // builds are swept: the sample directory is exactly one example file.
        File.Delete(path!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path!)!, "eqlog_Old_demo.txt"), "stale");
        Assert.Equal(path, sample.TryEnsureExtracted());
        var entry = Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(path!)!));
        Assert.Equal(path, entry);
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
    public void SampleFileNameParsesAsSampleCharacterAtDemo()
    {
        var sample = new SampleLog(_dir);
        var path = sample.TryEnsureExtracted();
        Assert.NotNull(path);
        var described = LogDiscovery.Describe(path!, "sample");
        Assert.NotNull(described);
        Assert.Equal("SampleCharacter", described!.Character);
        Assert.Equal("demo", described.Server);
        Assert.Equal("sample", described.Source);
    }
}
