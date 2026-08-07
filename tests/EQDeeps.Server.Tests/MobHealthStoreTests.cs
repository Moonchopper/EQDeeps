using EQDeeps.Core.Mobs;
using EQDeeps.Server;
using Xunit;

namespace EQDeeps.Server.Tests;

public sealed class MobHealthStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));

    private static readonly DateTime T0 = new(2026, 8, 3, 20, 0, 0);

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

    private static List<KillSample> Kills(int count, long damage, int? difficulty = 3) =>
        Enumerable.Range(0, count)
            .Select(i => new KillSample(
                "A dar ghoul knight", "The Ruins of Old Guk", difficulty,
                difficulty is null ? null : "Fused", damage, T0.AddMinutes(i)))
            .ToList();

    [Fact]
    public void LearnedHealthSurvivesARestart()
    {
        var first = new MobHealthStore(_dir);
        Assert.Equal(6, first.Record("xegony", Kills(6, 4000)));

        // A fresh store reading the same directory is what the next launch is.
        var second = new MobHealthStore(_dir);
        var estimate = Assert.Single(second.Estimates("xegony"));
        Assert.Equal(4000, estimate.Health);
        Assert.Equal(6, estimate.Samples);
    }

    /// <summary>
    /// Re-opening a log offers every kill in it again, and the server sweeps
    /// the fight list once a second on top of that. Both have to be free.
    /// </summary>
    [Fact]
    public void ReRecordingTheSameKillsChangesNothing()
    {
        var store = new MobHealthStore(_dir);
        var kills = Kills(5, 4000);

        Assert.Equal(5, store.Record("xegony", kills));
        Assert.Equal(0, store.Record("xegony", kills));
        Assert.Equal(5, Assert.Single(store.Estimates("xegony")).Samples);
    }

    /// <summary>
    /// Two servers are two worlds. A mob's health on one says nothing about
    /// the same name on the other.
    /// </summary>
    [Fact]
    public void ServersDoNotShareEvidence()
    {
        var store = new MobHealthStore(_dir);
        store.Record("xegony", Kills(5, 4000));
        store.Record("firiona", Kills(5, 900));

        Assert.Equal(4000, Assert.Single(store.Estimates("xegony")).Health);
        Assert.Equal(900, Assert.Single(store.Estimates("firiona")).Health);
    }

    [Fact]
    public void LookupKeysOnMobZoneAndDifficultyTogether()
    {
        var store = new MobHealthStore(_dir);
        store.Record("xegony", Kills(5, 900, difficulty: 1));
        store.Record("xegony", Kills(5, 4000, difficulty: 3));

        var lookup = store.Lookup("xegony");
        Assert.Equal(2, lookup.Count);
        Assert.Equal(900, lookup[MobHealthStore.KeyOf("A dar ghoul knight", "The Ruins of Old Guk", 1)].Health);
        Assert.Equal(4000, lookup[MobHealthStore.KeyOf("A dar ghoul knight", "The Ruins of Old Guk", 3)].Health);
        // The open world is a third bucket, and nothing has taught it yet.
        Assert.False(lookup.ContainsKey(
            MobHealthStore.KeyOf("A dar ghoul knight", "The Ruins of Old Guk", null)));
    }

    /// <summary>
    /// The index is a cache of things the logs still say, so a damaged file is
    /// worth exactly nothing and costs nothing — it must not be a permanent
    /// failure the user has to find and delete by hand.
    /// </summary>
    [Fact]
    public void CorruptFileStartsFreshInsteadOfFailingForever()
    {
        var store = new MobHealthStore(_dir);
        store.Record("xegony", Kills(5, 4000));

        File.WriteAllText(Path.Combine(_dir, "mobs", "xegony.json"), "{ not json");

        var reopened = new MobHealthStore(_dir);
        Assert.Empty(reopened.Estimates("xegony"));
        Assert.Equal(5, reopened.Record("xegony", Kills(5, 4000)));
    }

    [Fact]
    public void ServerNameNeverEscapesTheStoreDirectory()
    {
        var store = new MobHealthStore(_dir);
        store.Record(@"..\..\evil", Kills(5, 4000));

        var written = Directory.GetFiles(Path.Combine(_dir, "mobs"), "*.json");
        Assert.Equal(["evil.json"], written.Select(Path.GetFileName));
    }
}
