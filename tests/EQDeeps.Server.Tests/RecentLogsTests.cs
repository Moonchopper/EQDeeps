using EQDeeps.Server;
using Xunit;

namespace EQDeeps.Server.Tests;

public sealed class RecentLogsTests : IDisposable
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
    public void TouchIsMostRecentFirstDedupedAndCapped()
    {
        var recents = new RecentLogs(_dir);
        for (var i = 1; i <= 12; i++)
        {
            recents.Touch($@"C:\logs\eqlog_Char{i}_server.txt");
        }

        // Re-touching an existing path (case-insensitively) moves it to the front.
        recents.Touch(@"C:\LOGS\EQLOG_CHAR5_SERVER.TXT");

        var list = recents.List();
        Assert.Equal(10, list.Count); // capped
        Assert.Equal(@"C:\LOGS\EQLOG_CHAR5_SERVER.TXT", list[0]);
        Assert.Equal(@"C:\logs\eqlog_Char12_server.txt", list[1]);
        Assert.DoesNotContain(@"C:\logs\eqlog_Char1_server.txt", list); // aged out
        Assert.Equal(1, list.Count(p => p.Contains("Char5", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void PersistsAcrossInstancesAndSurvivesCorruption()
    {
        new RecentLogs(_dir).Touch(@"C:\logs\eqlog_Kizant_xegony.txt");
        Assert.Equal([@"C:\logs\eqlog_Kizant_xegony.txt"], new RecentLogs(_dir).List());

        File.WriteAllText(Path.Combine(_dir, "recent-logs.json"), "{not json");
        Assert.Empty(new RecentLogs(_dir).List()); // corrupt: fresh start, no throw
    }

    [Fact]
    public void ForgetRemovesOnlyThatPathAndPersists()
    {
        var recents = new RecentLogs(_dir);
        recents.Touch(@"C:\logs\eqlog_Keep_xegony.txt");
        recents.Touch(@"C:\logs\eqlog_Drop_testserver.txt");

        Assert.True(recents.Forget(@"c:\LOGS\eqlog_drop_testserver.TXT")); // case-insensitive
        Assert.Equal([@"C:\logs\eqlog_Keep_xegony.txt"], recents.List());
        Assert.False(recents.Forget(@"C:\logs\eqlog_Drop_testserver.txt")); // already gone

        // The removal survives a restart — otherwise it would come straight back.
        Assert.Equal([@"C:\logs\eqlog_Keep_xegony.txt"], new RecentLogs(_dir).List());
    }

    [Fact]
    public void DescribeParsesConventionAndFallsBackForOddNames()
    {
        Directory.CreateDirectory(_dir);
        var conventional = Path.Combine(_dir, "eqlog_Kizant_xegony.txt");
        File.WriteAllText(conventional, "");
        var described = LogDiscovery.Describe(conventional, "recent");
        Assert.NotNull(described);
        Assert.Equal("Kizant", described!.Character);
        Assert.Equal("xegony", described.Server);
        Assert.Equal("recent", described.Source);

        var odd = Path.Combine(_dir, "my-emu-log.txt");
        File.WriteAllText(odd, "");
        var oddDescribed = LogDiscovery.Describe(odd, "recent");
        Assert.NotNull(oddDescribed);
        Assert.Equal("my-emu-log", oddDescribed!.Character);
        Assert.Equal("unknown", oddDescribed.Server);

        Assert.Null(LogDiscovery.Describe(Path.Combine(_dir, "missing.txt"), "recent"));
    }
}
