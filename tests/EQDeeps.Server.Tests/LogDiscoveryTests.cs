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

    [Fact]
    public void InstallRootsNeverThrows()
    {
        // Same contract as Discover: enumerating drives and registry hives is
        // allowed to find nothing, never to blow up.
        Assert.NotNull(LogDiscovery.InstallRoots());
    }

    [Fact]
    public void FindsInstallsByProductFolderRatherThanExactName()
    {
        // The bug this encodes: the install is "EverQuest Legends", not
        // "EverQuest", and hardcoding the latter made it invisible whenever the
        // game wasn't already running.
        var parent = Path.Combine(Path.GetDirectoryName(_dir)!, "Installed Games");
        Directory.CreateDirectory(Path.Combine(parent, "EverQuest Legends", "Logs"));
        Directory.CreateDirectory(Path.Combine(parent, "EverQuest"));
        File.WriteAllText(Path.Combine(parent, "EverQuest", "eqgame.exe"), "stub");

        var found = LogDiscovery.ScanInstalledGames(parent);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, d => Path.GetFileName(d) == "EverQuest Legends");
        Assert.Contains(found, d => Path.GetFileName(d) == "EverQuest");
    }

    [Fact]
    public void SkipsUnrelatedAndEmptyGameFolders()
    {
        var parent = Path.Combine(Path.GetDirectoryName(_dir)!, "Installed Games");
        Directory.CreateDirectory(Path.Combine(parent, "PlanetSide 2", "Logs"));  // not EverQuest
        Directory.CreateDirectory(Path.Combine(parent, "EverQuest Next"));        // no Logs, no exe

        Assert.Empty(LogDiscovery.ScanInstalledGames(parent));
    }

    [Fact]
    public void MissingInstalledGamesParentYieldsNothing()
    {
        Assert.Empty(LogDiscovery.ScanInstalledGames(Path.Combine(_dir, "nope")));
    }
}
