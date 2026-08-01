using EQDeeps.Server;
using Xunit;

namespace EQDeeps.Server.Tests;

public sealed class LogDiscoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"), "Logs");

    public LogDiscoveryTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ScansOnlyParseableCharacterLogs()
    {
        File.WriteAllText(Path.Combine(_dir, "eqlog_Kizant_xegony.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "eqlog_Soandso_firiona.txt"), "y");
        File.WriteAllText(Path.Combine(_dir, "eqlog_NoServer.txt"), "junk");   // not the convention
        File.WriteAllText(Path.Combine(_dir, "dbg.txt"), "junk");              // not a character log

        var found = LogDiscovery.ScanLogsDirectory(_dir, "test");

        Assert.Equal(2, found.Count);
        Assert.Contains(found, l => l is { Character: "Kizant", Server: "xegony", Source: "test" });
        Assert.Contains(found, l => l is { Character: "Soandso", Server: "firiona" });
        Assert.All(found, l => Assert.True(File.Exists(l.Path)));
    }

    [Fact]
    public void MissingDirectoryYieldsNothing()
    {
        Assert.Empty(LogDiscovery.ScanLogsDirectory(Path.Combine(_dir, "nope"), "test"));
    }

    [Fact]
    public void DiscoverNeverThrows()
    {
        // Environment-dependent sources (running game, registry, install paths)
        // must degrade to "nothing found", never to an exception.
        var results = LogDiscovery.Discover();
        Assert.NotNull(results);
    }
}
