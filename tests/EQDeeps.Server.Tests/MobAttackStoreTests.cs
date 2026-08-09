using EQDeeps.Core.Mobs;
using Xunit;

namespace EQDeeps.Server.Tests;

public sealed class MobAttackStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 8, 8, 20, 0, 0);

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

    /// <summary>One fight per minute, each of them landing <paramref name="hits"/> identical crushes.</summary>
    private static List<AttackSample> Fights(
        int count, long hit, int hits = 3, int? level = 55, int? difficulty = 3)
    {
        var samples = new List<AttackSample>();
        for (var i = 0; i < count; i++)
        {
            var tally = new SkillTally();
            for (var h = 0; h < hits; h++)
            {
                tally.Swings++;
                tally.Record(hit);
            }

            var begin = T0.AddMinutes(i);
            samples.Add(new AttackSample(
                "A dar ghoul knight", "The Ruins of Old Guk", difficulty,
                difficulty is null ? null : "Fused", level, "Kizant",
                begin, begin.AddSeconds(30),
                new Dictionary<string, SkillTally> { ["Crushes"] = tally }));
        }

        return samples;
    }

    [Fact]
    public void LearnedProfilesSurviveARestart()
    {
        var first = new MobAttackStore(_dir);
        Assert.Equal(6, first.Record("xegony", Fights(6, 200)));

        // A fresh store reading the same directory is what the next launch is.
        var second = new MobAttackStore(_dir);
        var estimate = Assert.Single(second.Estimates("xegony"));
        Assert.Equal(6, estimate.Fights);
        Assert.Equal(18, estimate.Landed);
        Assert.Equal(200, estimate.AvgHit);
        Assert.InRange(estimate.MedianHit, 180, 220);
    }

    /// <summary>
    /// Re-opening a log offers every fight in it again, and the server sweeps
    /// once a second on top of that. The tally is cumulative, so a double count
    /// could never be undone — this must be free and it must be exact.
    /// </summary>
    [Fact]
    public void ReRecordingTheSameFightsChangesNothing()
    {
        var store = new MobAttackStore(_dir);
        var fights = Fights(5, 200);

        Assert.Equal(5, store.Record("xegony", fights));
        Assert.Equal(0, store.Record("xegony", fights));
        Assert.Equal(0, store.Record("xegony", fights));

        var estimate = Assert.Single(store.Estimates("xegony"));
        Assert.Equal(5, estimate.Fights);
        Assert.Equal(15, estimate.Landed);
    }

    /// <summary>Two servers are two worlds, even for a mob of the same name.</summary>
    [Fact]
    public void ServersDoNotShareEvidence()
    {
        var store = new MobAttackStore(_dir);
        store.Record("xegony", Fights(5, 200));
        store.Record("firiona", Fights(5, 50));

        Assert.Equal(200, Assert.Single(store.Estimates("xegony")).AvgHit);
        Assert.Equal(50, Assert.Single(store.Estimates("firiona")).AvgHit);
    }

    /// <summary>
    /// Two characters on one account are two rows, because how hard a mob hits
    /// is a fact about the pairing. The store shares a file; the key does not
    /// share a bucket.
    /// </summary>
    [Fact]
    public void CharactersOfDifferentLevelsDoNotAverageTogether()
    {
        var store = new MobAttackStore(_dir);
        store.Record("xegony", Fights(5, 400, level: 40));
        store.Record("xegony", Fights(5, 100, level: 60));

        var byLevel = new MobAttackStore(_dir).Estimates("xegony")
            .ToDictionary(e => e.DefenderLevel!.Value, e => e.AvgHit);
        Assert.Equal(2, byLevel.Count);
        Assert.Equal(400, byLevel[40]);
        Assert.Equal(100, byLevel[60]);
    }

    /// <summary>
    /// The index is a cache of things the logs still say, so a damaged file is
    /// worth nothing and costs nothing — it must not be a permanent failure the
    /// user has to find and delete by hand.
    /// </summary>
    [Fact]
    public void CorruptFileStartsFreshInsteadOfFailingForever()
    {
        var store = new MobAttackStore(_dir);
        store.Record("xegony", Fights(5, 200));

        File.WriteAllText(Path.Combine(_dir, "attacks", "xegony.json"), "{ not json");

        var reopened = new MobAttackStore(_dir);
        Assert.Empty(reopened.Estimates("xegony"));
        Assert.Equal(5, reopened.Record("xegony", Fights(5, 200)));
    }

    [Fact]
    public void ServerNameNeverEscapesTheStoreDirectory()
    {
        var store = new MobAttackStore(_dir);
        store.Record(@"..\..\evil", Fights(5, 200));

        var written = Directory.GetFiles(Path.Combine(_dir, "attacks"), "*.json");
        Assert.Equal(["evil.json"], written.Select(Path.GetFileName));
    }

    /// <summary>Nothing learned yet is an empty answer, not a missing file blowing up.</summary>
    [Fact]
    public void UnknownServerHasNoProfiles()
    {
        Assert.Empty(new MobAttackStore(_dir).Estimates("nobody"));
    }
}
