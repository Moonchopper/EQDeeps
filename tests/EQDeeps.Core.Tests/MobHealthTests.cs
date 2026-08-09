using EQDeeps.Core.Events;
using EQDeeps.Core.Mobs;
using EQDeeps.Core.Parsing;
using EQDeeps.Core.Sessions;
using Xunit;

namespace EQDeeps.Core.Tests;

public class InstanceZoneTests
{
    [Theory]
    [InlineData("The Estate of Unrest 4 (Refined)", "The Estate of Unrest", 4, "Refined")]
    [InlineData("The City of Guk 1 (Awakened)", "The City of Guk", 1, "Awakened")]
    [InlineData("The Ruins of Old Guk 3 (Fused)", "The Ruins of Old Guk", 3, "Fused")]
    [InlineData("Nagafen's Lair 2 (Adaptive)", "Nagafen's Lair", 2, "Adaptive")]
    public void SplitsInstanceSuffixIntoPlaceAndDifficulty(
        string logged, string expectedBase, int expectedDifficulty, string expectedTier)
    {
        var zone = InstanceZone.Parse(logged);

        Assert.Equal(expectedBase, zone.BaseName);
        Assert.Equal(expectedDifficulty, zone.Difficulty);
        Assert.Equal(expectedTier, zone.TierName);
        Assert.Equal(logged, zone.Display);
    }

    /// <summary>
    /// The open world has no suffix, and neither does a tier-0 instance — the
    /// client writes them identically. Null rather than 0 says what was
    /// observed: no instance line, not "difficulty zero".
    /// </summary>
    [Theory]
    [InlineData("The Estate of Unrest")]
    [InlineData("Butcherblock Mountains")]
    [InlineData("Dagnor's Cauldron")]
    public void PlainZoneNameCarriesNoDifficulty(string logged)
    {
        var zone = InstanceZone.Parse(logged);

        Assert.Equal(logged, zone.BaseName);
        Assert.Null(zone.Difficulty);
        Assert.Null(zone.TierName);
        Assert.Equal(logged, zone.Display);
    }

    /// <summary>
    /// A tier the server adds or renames has to survive as itself. Nothing here
    /// maps the word against a list, so "5 (Ascendant)" reads as tier 5 even
    /// though no such tier existed when this was written.
    /// </summary>
    [Fact]
    public void UnknownTierNameIsCarriedThroughRatherThanRejected()
    {
        var zone = InstanceZone.Parse("The Plane of Fear 5 (Ascendant)");

        Assert.Equal("The Plane of Fear", zone.BaseName);
        Assert.Equal(5, zone.Difficulty);
        Assert.Equal("Ascendant", zone.TierName);
    }

    [Theory]
    [InlineData("Some Zone (Refined)")]        // no number
    [InlineData("Some Zone 4")]                // no tier word
    [InlineData("Some Zone 4 (Refined) East")] // suffix is not trailing
    [InlineData("Some Zone 4 (12)")]           // tier word is not a word
    public void NearMissesAreNotReadAsInstances(string logged)
    {
        var zone = InstanceZone.Parse(logged);

        Assert.Equal(logged, zone.BaseName);
        Assert.Null(zone.Difficulty);
    }
}

public class MobHealthIndexTests
{
    private static readonly DateTime T0 = new(2026, 8, 3, 20, 0, 0);

    private static KillSample Kill(long damage, int minute, int? difficulty = 3) =>
        new("A dar ghoul knight", "The Ruins of Old Guk", difficulty,
            difficulty is null ? null : "Fused", damage, T0.AddMinutes(minute));

    /// <summary>Kills spaced past the concurrency window, so all of them count.</summary>
    private static List<KillSample> Spaced(IEnumerable<long> damages, int? difficulty = 3)
    {
        var minute = 0;
        return damages.Select(d => Kill(d, minute++, difficulty)).ToList();
    }

    [Fact]
    public void EstimatesHealthAsTheMedianDamageToKill()
    {
        var index = new MobHealthIndex();
        index.Add(Spaced([1000, 1100, 1200, 1300, 1400]));

        var estimate = Assert.Single(index.Estimates());
        Assert.Equal(1200, estimate.Health);
        Assert.Equal(1000, estimate.Floor);
        Assert.Equal(1400, estimate.Ceiling);
        Assert.Equal(5, estimate.Samples);
        Assert.Equal(5, estimate.CleanSamples);
    }

    /// <summary>
    /// The same mob at two difficulties is two mobs as far as this is
    /// concerned. Merging them would produce a number that is wrong for both,
    /// which is the entire reason difficulty is read off the zone line.
    /// </summary>
    [Fact]
    public void DifficultySeparatesOtherwiseIdenticalMobs()
    {
        var index = new MobHealthIndex();
        index.Add(Spaced([1000, 1000, 1000, 1000, 1000], difficulty: 1));
        index.Add(Spaced([4000, 4000, 4000, 4000, 4000], difficulty: 3));

        var estimates = index.Estimates().OrderBy(e => e.Difficulty).ToList();
        Assert.Equal(2, estimates.Count);
        Assert.Equal((1, 1000L), (estimates[0].Difficulty, estimates[0].Health));
        Assert.Equal((3, 4000L), (estimates[1].Difficulty, estimates[1].Health));
    }

    /// <summary>The open world and an instance of the same zone are separate too.</summary>
    [Fact]
    public void OpenWorldIsItsOwnBucket()
    {
        var index = new MobHealthIndex();
        index.Add(Spaced([2000, 2000, 2000, 2000], difficulty: null));
        index.Add(Spaced([3000, 3000, 3000, 3000], difficulty: 2));

        Assert.Equal(2, index.Estimates().Count);
        Assert.Single(index.Estimates(), e => e.Difficulty is null && e.Health == 2000);
    }

    /// <summary>
    /// Most recently killed first, not best-known first. The index belongs to
    /// the server and accumulates for months, so ranking by confidence would
    /// bury tonight's camp under every zone the account has ever worked.
    /// Confidence is a column; it does not also need to be the order.
    /// </summary>
    [Fact]
    public void MostRecentlyKilledComesFirst()
    {
        var index = new MobHealthIndex();

        // A camp worked to death last week: twelve clean kills, High confidence.
        index.Add(Enumerable.Range(0, 12).Select(i => new KillSample(
            "An old favourite", "The Ruins of Old Guk", 3, "Fused", 1000, T0.AddMinutes(i))));

        // Tonight's mob, killed once: Low, and the row that matters.
        index.Add([new KillSample(
            "Tonight's problem", "The Ruins of Old Guk", 3, "Fused", 4000, T0.AddDays(7))]);

        var estimates = index.Estimates();
        Assert.Equal("Tonight's problem", estimates[0].Mob);
        Assert.Equal(MobHealthConfidence.Low, estimates[0].Confidence);
        Assert.Equal("An old favourite", estimates[1].Mob);
        Assert.Equal(MobHealthConfidence.High, estimates[1].Confidence);
    }

    /// <summary>
    /// Two mobs of one name up at once are one fight, so the first death banks
    /// both their damage and the survivor's remainder banks far too little.
    /// Adjacent kills are discarded in pairs; here the pair is 9000 and 100,
    /// and dropping them leaves the four honest kills.
    /// </summary>
    [Fact]
    public void KillsCrowdedTogetherAreDiscardedAsMergedFights()
    {
        var index = new MobHealthIndex();
        index.Add([
            Kill(1000, 0),
            Kill(1000, 5),
            Kill(1000, 10),
            Kill(1000, 15),
            // Ten seconds apart: one pull that had two of them up.
            new("A dar ghoul knight", "The Ruins of Old Guk", 3, "Fused", 9000, T0.AddMinutes(20)),
            new("A dar ghoul knight", "The Ruins of Old Guk", 3, "Fused", 100, T0.AddMinutes(20).AddSeconds(10)),
        ]);

        var estimate = Assert.Single(index.Estimates());
        Assert.Equal(6, estimate.Samples);
        Assert.Equal(4, estimate.CleanSamples);
        Assert.Equal(1000, estimate.Health);
    }

    /// <summary>
    /// With barely any kills the filter would leave nothing at all, so it is
    /// skipped and the confidence carries the warning instead. Reporting a
    /// rough number beats reporting none.
    /// </summary>
    [Fact]
    public void FallsBackToEverySampleWhenTheFilterWouldEmptyTheBucket()
    {
        var index = new MobHealthIndex();
        index.Add([
            Kill(1000, 0),
            new("A dar ghoul knight", "The Ruins of Old Guk", 3, "Fused", 1200, T0.AddSeconds(5)),
        ]);

        var estimate = Assert.Single(index.Estimates());
        Assert.Equal(0, estimate.CleanSamples);
        Assert.Equal(2, estimate.Samples);
        // Nearest-rank, so an even count takes the lower middle.
        Assert.Equal(1000, estimate.Health);
        Assert.Equal(MobHealthConfidence.Low, estimate.Confidence);
    }

    [Fact]
    public void ManyAgreeingKillsGradeHighAndScatteredOnesDoNot()
    {
        var tight = new MobHealthIndex();
        tight.Add(Spaced(Enumerable.Range(0, 12).Select(i => 1000L + i * 10)));
        Assert.Equal(MobHealthConfidence.High, tight.Estimates()[0].Confidence);

        var scattered = new MobHealthIndex();
        scattered.Add(Spaced(Enumerable.Range(0, 12).Select(i => 500L + i * 500)));
        Assert.Equal(MobHealthConfidence.Low, scattered.Estimates()[0].Confidence);
    }

    /// <summary>
    /// Re-opening a log replays every kill in it. That must cost nothing —
    /// otherwise a player who reloads their log three times has an index that
    /// thinks they killed everything three times.
    /// </summary>
    [Fact]
    public void ReplayingTheSameKillsAddsNothing()
    {
        var index = new MobHealthIndex();
        var samples = Spaced([1000, 1100, 1200, 1300]);

        Assert.Equal(4, index.Add(samples));
        Assert.Equal(0, index.Add(samples));
        Assert.Equal(4, index.SampleCount);
    }

    [Fact]
    public void KeepsOnlyTheMostRecentSamplesPerMob()
    {
        var index = new MobHealthIndex();
        index.Add(Spaced(Enumerable.Repeat(1000L, MobHealthIndex.SamplesPerMob + 50)));

        Assert.Equal(MobHealthIndex.SamplesPerMob, index.SampleCount);
        // The oldest went first, so the surviving window ends at the newest kill.
        Assert.Equal(
            T0.AddMinutes(MobHealthIndex.SamplesPerMob + 49),
            index.Snapshot().Max(s => s.KilledAt));
    }

    [Fact]
    public void HarvestTakesOnlyDamagedKillsThatHappenedSomewhereKnown()
    {
        var identity = new IdentityRegistry();
        identity.AddVerifiedPlayer("Raider01");
        var tracker = new FightTracker(identity);

        // Before any zone line: killed, but nowhere we can name.
        tracker.Process(T0, new DamageEvent("Raider01", "A ghoul", 300, DamageKind.Melee, "Crushes"));
        tracker.Process(T0.AddSeconds(1), new DeathEvent("A ghoul", "Raider01"));

        tracker.Process(T0.AddMinutes(1), new ZoneEvent("The Estate of Unrest 2 (Adaptive)"));
        tracker.Process(T0.AddMinutes(2), new DamageEvent("Raider01", "A ghoul", 500, DamageKind.Melee, "Crushes"));
        tracker.Process(T0.AddMinutes(2).AddSeconds(1), new DeathEvent("A ghoul", "Raider01"));

        // Fought but survived (fight closes on the zone line, not a death).
        tracker.Process(T0.AddMinutes(3), new DamageEvent("Raider01", "A ghast", 90, DamageKind.Melee, "Crushes"));
        tracker.Process(T0.AddMinutes(4), new ZoneEvent(null));

        var sample = Assert.Single(MobHealthIndex.Harvest(tracker.Fights));
        Assert.Equal("A ghoul", sample.Mob);
        Assert.Equal("The Estate of Unrest", sample.Zone);
        Assert.Equal(2, sample.Difficulty);
        Assert.Equal("Adaptive", sample.TierName);
        Assert.Equal(500, sample.Damage);
    }

    /// <summary>
    /// A load screen means the old zone has stopped being true. A fight that
    /// starts before the new one is named must not inherit the old one, or its
    /// kill lands in the wrong instance's bucket.
    /// </summary>
    [Fact]
    public void ZoningOutClearsTheZoneRatherThanLettingItStick()
    {
        var identity = new IdentityRegistry();
        identity.AddVerifiedPlayer("Raider01");
        var tracker = new FightTracker(identity);

        tracker.Process(T0, new ZoneEvent("The City of Guk 4 (Refined)"));
        tracker.Process(T0.AddSeconds(10), new ZoneEvent(null));
        tracker.Process(T0.AddSeconds(20), new DamageEvent("Raider01", "A ghoul", 100, DamageKind.Melee, "Crushes"));

        Assert.Null(Assert.Single(tracker.Fights).Zone);
    }

    /// <summary>
    /// The server sweeps the fight list every second, so a sweep has to be able
    /// to say "only what is new" rather than rebuilding the whole history.
    /// </summary>
    [Fact]
    public void HarvestSinceReturnsOnlyKillsAfterTheWatermark()
    {
        var identity = new IdentityRegistry();
        identity.AddVerifiedPlayer("Raider01");
        var tracker = new FightTracker(identity);
        tracker.Process(T0, new ZoneEvent("The City of Guk 4 (Refined)"));

        foreach (var minute in new[] { 1, 5, 9 })
        {
            var at = T0.AddMinutes(minute);
            tracker.Process(at, new DamageEvent("Raider01", "A ghoul", 500, DamageKind.Melee, "Crushes"));
            tracker.Process(at.AddSeconds(1), new DeathEvent("A ghoul", "Raider01"));
        }

        Assert.Equal(3, MobHealthIndex.Harvest(tracker.Fights).Count);

        var later = MobHealthIndex.Harvest(tracker.Fights, T0.AddMinutes(5).AddSeconds(1));
        Assert.Equal(T0.AddMinutes(9).AddSeconds(1), Assert.Single(later).KilledAt);
    }
}
