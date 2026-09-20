using EQDeeps.Core.Events;
using EQDeeps.Core.Mobs;
using EQDeeps.Core.Sessions;
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

    /// <summary>
    /// End-to-end proof of C4: a kill in the group-scaled instance and the
    /// same mob killed later in the ordinary (unmarked) zone must key apart
    /// all the way from <see cref="FightTracker"/> through
    /// <see cref="MobHealthIndex.Harvest"/> to
    /// <see cref="MobHealthStore.KeyOf"/> — this is what "a solo-scaled boss
    /// and a group-scaled one are different fights" (C4) means in practice,
    /// not just at the <c>InstanceZone</c> unit level.
    /// </summary>
    [Fact]
    public void MarkedAndUnmarkedInstancesKeepSeparateKeysEndToEnd()
    {
        var identity = new IdentityRegistry();
        identity.AddVerifiedPlayer("Raider01");
        var tracker = new FightTracker(identity);

        // Killed in the group-scaled instance.
        tracker.Process(T0, new ZoneEvent("The Plane of Fear - Group 3 (Fused)"));
        tracker.Process(T0.AddSeconds(1), new DamageEvent("Raider01", "A tormentor", 500, DamageKind.Melee, "Crushes"));
        tracker.Process(T0.AddSeconds(2), new DeathEvent("A tormentor", "Raider01"));

        // Same mob, same tier, killed later in the open (unmarked) instance.
        tracker.Process(T0.AddMinutes(5), new ZoneEvent("The Plane of Fear 3 (Fused)"));
        tracker.Process(T0.AddMinutes(5).AddSeconds(1), new DamageEvent("Raider01", "A tormentor", 900, DamageKind.Melee, "Crushes"));
        tracker.Process(T0.AddMinutes(5).AddSeconds(2), new DeathEvent("A tormentor", "Raider01"));

        var samples = MobHealthIndex.Harvest(tracker.Fights);
        Assert.Equal(2, samples.Count);

        var marked = Assert.Single(samples, s => s.Zone.Contains(" - Group"));
        var unmarked = Assert.Single(samples, s => !s.Zone.Contains(" - Group"));
        Assert.Equal("The Plane of Fear - Group", marked.Zone);
        Assert.Equal("The Plane of Fear", unmarked.Zone);
        Assert.Equal(3, marked.Difficulty);
        Assert.Equal(3, unmarked.Difficulty);

        var markedKey = MobHealthStore.KeyOf(marked.Mob, marked.Zone, marked.Difficulty);
        var unmarkedKey = MobHealthStore.KeyOf(unmarked.Mob, unmarked.Zone, unmarked.Difficulty);
        Assert.NotEqual(markedKey, unmarkedKey);

        // And feeding both through the real store proves they land as two
        // separate learned estimates rather than one blended number.
        var store = new MobHealthStore(_dir);
        store.Record("test-server", samples);
        Assert.Equal(2, store.Estimates("test-server").Count);
    }
}
