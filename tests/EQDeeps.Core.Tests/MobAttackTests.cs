using System.Text.Json;
using EQDeeps.Core.Events;
using EQDeeps.Core.Mobs;
using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;
using Xunit;

namespace EQDeeps.Core.Tests;

public class HitHistogramTests
{
    /// <summary>
    /// Buckets are proportional, not fixed-width — which is the whole point of
    /// log spacing. A hit is recovered to within about a tenth of itself at
    /// twelve points and at twelve thousand.
    /// </summary>
    [Theory]
    [InlineData(12)]
    [InlineData(120)]
    [InlineData(1200)]
    [InlineData(12000)]
    [InlineData(120000)]
    public void RecoversAValueToWithinABucketWidth(long amount)
    {
        var recovered = HitHistogram.ValueOf(HitHistogram.BucketOf(amount));

        Assert.InRange(recovered, amount * 0.85, amount * 1.15);
    }

    [Fact]
    public void QuantilesReadOffTheHistogramInOrder()
    {
        var histogram = new Dictionary<int, int>();
        foreach (var amount in new long[] { 100, 100, 100, 200, 200, 400, 400, 800, 800, 1600 })
        {
            var bucket = HitHistogram.BucketOf(amount);
            histogram[bucket] = histogram.GetValueOrDefault(bucket) + 1;
        }

        Assert.InRange(HitHistogram.Quantile(histogram, 0.10), 90, 110);
        Assert.InRange(HitHistogram.Quantile(histogram, 0.50), 180, 220);
        Assert.InRange(HitHistogram.Quantile(histogram, 0.90), 720, 880);
    }

    [Fact]
    public void EmptyHistogramHasNoQuantile()
    {
        Assert.Equal(0, HitHistogram.Quantile([], 0.5));
    }

    /// <summary>
    /// A hit past the top of the scale clamps rather than indexing off the end.
    /// Nothing in EverQuest hits for a million, but a parser bug reading a
    /// column wrong should cost a wrong row, not a crash.
    /// </summary>
    [Fact]
    public void ImplausiblyLargeHitClampsToTheTopBucket()
    {
        Assert.Equal(HitHistogram.Buckets - 1, HitHistogram.BucketOf(long.MaxValue / 2));
    }
}

public class DefenderLevelsTests
{
    private static readonly DateTime T0 = new(2026, 8, 8, 20, 0, 0);

    private static RecordStore Store(params (int Minute, GameEvent Event)[] events)
    {
        var store = new RecordStore();
        foreach (var (minute, evt) in events)
        {
            store.Append(T0.AddMinutes(minute), evt);
        }

        return store;
    }

    /// <summary>A ding fixes the level from that moment and says nothing about before it.</summary>
    [Fact]
    public void DingAppliesForwardOnly()
    {
        var levels = DefenderLevels.Build(
            Store((10, new LevelEvent(42)), (20, new LevelEvent(43))), "Kazint");

        Assert.Null(levels.LevelOf("Kazint", T0));
        Assert.Equal(42, levels.LevelOf("Kazint", T0.AddMinutes(15)));
        Assert.Equal(43, levels.LevelOf("Kazint", T0.AddMinutes(25)));
    }

    /// <summary>
    /// A /who reports a level that was already true, so the first one read is
    /// read backwards as well. Without this a player who types /who once at
    /// nine in the evening has no level for everything before it, which is most
    /// of the log.
    /// </summary>
    [Fact]
    public void FirstWhoIsReadBackwardsOverTheLogBeforeIt()
    {
        var levels = DefenderLevels.Build(
            Store((60, new WhoEvent("Kazint", 55, "Warrior"))), "Kazint");

        Assert.Equal(55, levels.LevelOf("Kazint", T0));
        Assert.Equal(55, levels.LevelOf("Kazint", T0.AddMinutes(120)));
    }

    /// <summary>A /who prints everyone in the zone, and everyone's level is theirs.</summary>
    [Fact]
    public void OtherPlayersGetLevelsFromWho()
    {
        var levels = DefenderLevels.Build(
            Store(
                (5, new WhoEvent("Vandil", 51, "Cleric")),
                (5, new WhoEvent("Kazint", 55, "Warrior"))),
            "Kazint");

        Assert.Equal(51, levels.LevelOf("Vandil", T0.AddMinutes(30)));
        Assert.Equal(55, levels.LevelOf("Kazint", T0.AddMinutes(30)));
    }

    /// <summary>
    /// Anonymous players, pets and anyone the log never levelled resolve to
    /// null. Null is a bucket of its own downstream — folding them into the
    /// owner's level would invent the one thing that was not observed.
    /// </summary>
    [Fact]
    public void NeverStatedLevelIsNullRatherThanTheOwners()
    {
        var levels = DefenderLevels.Build(Store((10, new LevelEvent(42))), "Kazint");

        Assert.Null(levels.LevelOf("Vandil", T0.AddMinutes(30)));
        Assert.Null(levels.LevelOf("Kazint`s pet", T0.AddMinutes(30)));
    }

    /// <summary>A ding is never backdated, even when it is the only thing on record.</summary>
    [Fact]
    public void DingIsNotReadBackwards()
    {
        var levels = DefenderLevels.Build(Store((60, new LevelEvent(42))), "Kazint");

        Assert.Null(levels.LevelOf("Kazint", T0));
    }
}

public class MobAttackIndexTests
{
    private static readonly DateTime T0 = new(2026, 8, 8, 20, 0, 0);

    private static SkillTally Melee(params long[] hits)
    {
        var tally = new SkillTally();
        foreach (var hit in hits)
        {
            tally.Swings++;
            tally.Record(hit);
        }

        return tally;
    }

    private static AttackSample Sample(
        SkillTally tally,
        int minute = 0,
        int? level = 55,
        int? difficulty = 3,
        string skill = "Crushes",
        string defender = "Kazint") =>
        new("A dar ghoul knight", "The Ruins of Old Guk", difficulty,
            difficulty is null ? null : "Fused", level, defender,
            T0.AddMinutes(minute), T0.AddMinutes(minute).AddSeconds(45),
            new Dictionary<string, SkillTally> { [skill] = tally });

    [Fact]
    public void ReportsTheMedianHitWithItsBand()
    {
        var index = new MobAttackIndex();
        index.Add([Sample(Melee(100, 200, 200, 200, 400))]);

        var estimate = Assert.Single(index.Estimates());
        Assert.Equal(5, estimate.Landed);
        Assert.Equal(1100, estimate.Total);
        Assert.Equal(220, estimate.AvgHit);
        Assert.InRange(estimate.MedianHit, 180, 220);
        Assert.InRange(estimate.Floor, 90, 110);
        Assert.InRange(estimate.Ceiling, 360, 440);
        Assert.Equal(400, estimate.MaxHit);
        Assert.Equal(100, estimate.MinHit);
    }

    /// <summary>
    /// The point of the whole defender-level axis: two characters of different
    /// levels are two rows, because a single average across them would describe
    /// neither of them.
    /// </summary>
    [Fact]
    public void DefenderLevelSeparatesOtherwiseIdenticalMobs()
    {
        var index = new MobAttackIndex();
        index.Add([Sample(Melee(400, 400, 400), level: 40)]);
        index.Add([Sample(Melee(100, 100, 100), minute: 5, level: 60)]);

        var estimates = index.Estimates().OrderBy(e => e.DefenderLevel).ToList();
        Assert.Equal(2, estimates.Count);
        Assert.Equal((40, 400d), (estimates[0].DefenderLevel, estimates[0].AvgHit));
        Assert.Equal((60, 100d), (estimates[1].DefenderLevel, estimates[1].AvgHit));
    }

    /// <summary>An unknown defender level is its own bucket, never the owner's.</summary>
    [Fact]
    public void UnknownDefenderLevelIsItsOwnRow()
    {
        var index = new MobAttackIndex();
        index.Add([Sample(Melee(300, 300), level: 55)]);
        index.Add([Sample(Melee(300, 300), minute: 5, level: null, defender: "Vandil")]);

        Assert.Equal(2, index.KeyCount);
        Assert.Single(index.Estimates(), e => e.DefenderLevel is null);
    }

    [Fact]
    public void DifficultySeparatesOtherwiseIdenticalMobs()
    {
        var index = new MobAttackIndex();
        index.Add([Sample(Melee(100, 100), difficulty: 1)]);
        index.Add([Sample(Melee(300, 300), minute: 5, difficulty: 3)]);

        var estimates = index.Estimates().OrderBy(e => e.Difficulty).ToList();
        Assert.Equal(2, estimates.Count);
        Assert.Equal(100d, estimates[0].AvgHit);
        Assert.Equal(300d, estimates[1].AvgHit);
    }

    /// <summary>
    /// Re-opening a log replays every fight in it. The tally is cumulative, so
    /// counting one twice can never be undone — this is the invariant the whole
    /// idempotency-by-fight-start scheme exists to hold.
    /// </summary>
    [Fact]
    public void ReplayingTheSameFightBanksItOnce()
    {
        var index = new MobAttackIndex();
        var samples = new[] { Sample(Melee(100, 200), minute: 0), Sample(Melee(300), minute: 5) };

        Assert.Equal(2, index.Add(samples));
        Assert.Equal(0, index.Add(samples));
        Assert.Equal(0, index.Add(samples));

        var estimate = Assert.Single(index.Estimates());
        Assert.Equal(3, estimate.Landed);
        Assert.Equal(600, estimate.Total);
        Assert.Equal(2, estimate.Fights);
    }

    /// <summary>
    /// Two defenders in one fight are two samples, because the mob's numbers
    /// against each of them are different facts — and they must not collide on
    /// the fight start they share.
    /// </summary>
    [Fact]
    public void TwoDefendersInOneFightAreTwoSamples()
    {
        var index = new MobAttackIndex();
        Assert.Equal(2, index.Add([
            Sample(Melee(100, 100), defender: "Kazint", level: 55),
            Sample(Melee(400, 400), defender: "Vandil", level: 40),
        ]));

        Assert.Equal(2, index.KeyCount);
    }

    /// <summary>
    /// Rates are over melee swings only. A mob's spell has no attempt anyone
    /// can dodge, and letting it into the denominator would make a caster look
    /// evasion-proof.
    /// </summary>
    [Fact]
    public void AvoidanceRatesCountMeleeSwingsOnly()
    {
        var melee = Melee(100, 100, 100, 100, 100, 100);
        melee.Swings += 4;
        melee.Misses += 2;
        melee.Dodges += 1;
        melee.Parries += 1;

        var spell = new SkillTally { Spell = true };
        spell.Record(500);

        var index = new MobAttackIndex();
        index.Add([
            new AttackSample(
                "A dar ghoul knight", "The Ruins of Old Guk", 3, "Fused", 55, "Kazint",
                T0, T0.AddSeconds(45),
                new Dictionary<string, SkillTally> { ["Crushes"] = melee, ["Ancient Breath"] = spell }),
        ]);

        var estimate = Assert.Single(index.Estimates());
        Assert.Equal(60, estimate.HitRate);
        Assert.Equal(20, estimate.MissRate);
        Assert.Equal(10, estimate.DodgeRate);
        Assert.Equal(10, estimate.ParryRate);
        Assert.Equal(7, estimate.Landed);   // the spell still lands
        Assert.Equal(1100, estimate.Total); // and still counts as damage

        // But it stays out of the headline: 600 from six 100-point swings, and
        // the 500-point nuke reported separately rather than dragging the
        // average to 157.
        Assert.Equal(100, estimate.AvgHit);
        Assert.Equal(6, estimate.MeleeHits);
        Assert.Equal(600, estimate.MeleeTotal);
        Assert.Equal(500, estimate.SpellTotal);
    }

    /// <summary>
    /// The case a real log made: 209 punches averaging 66, 752 damage-shield
    /// ticks averaging 15, and four 582-point nukes. Pooled, the headline reads
    /// "average hit 35" — a number true of none of the three and dominated by
    /// the one the mob is not choosing to do.
    /// </summary>
    [Fact]
    public void DamageShieldTicksDoNotDragDownTheHeadlineSwing()
    {
        var punches = Melee([.. Enumerable.Repeat(66L, 209)]);
        var shield = new SkillTally { Spell = true };
        foreach (var _ in Enumerable.Range(0, 752))
        {
            shield.Record(15);
        }

        var index = new MobAttackIndex();
        index.Add([
            new AttackSample(
                "A forsaken revenant", "The Plane of Hate", null, null, 20, "Kazint",
                T0, T0.AddSeconds(45),
                new Dictionary<string, SkillTally>
                {
                    ["Hits"] = punches,
                    ["Damage shield"] = shield,
                }),
        ]);

        var estimate = Assert.Single(index.Estimates());
        Assert.Equal(66, estimate.AvgHit);
        Assert.Equal(66, estimate.MaxHit);
        Assert.Equal(209, estimate.MeleeHits);
        Assert.Equal(961, estimate.Landed);
        Assert.Equal(11280, estimate.SpellTotal);
        // And the grade is earned by the swings, not by the shield's volume.
        Assert.Equal(MobAttackConfidence.High, estimate.Confidence);
    }

    /// <summary>Each attack is reported on its own, biggest contributor first.</summary>
    [Fact]
    public void SkillsAreBrokenOutAndRankedByDamage()
    {
        var index = new MobAttackIndex();
        index.Add([
            new AttackSample(
                "A dar ghoul knight", "The Ruins of Old Guk", 3, "Fused", 55, "Kazint",
                T0, T0.AddSeconds(45),
                new Dictionary<string, SkillTally>
                {
                    ["Crushes"] = Melee(100, 100),
                    ["Bashes"] = Melee(900),
                }),
        ]);

        var estimate = Assert.Single(index.Estimates());
        Assert.Equal(["Bashes", "Crushes"], estimate.Skills.Select(s => s.Skill));
        Assert.Equal(900, estimate.Skills[0].Total);
    }

    /// <summary>
    /// Confidence is graded on evidence and on knowing who was hit — never on
    /// spread, which for a mob's melee is the answer rather than the doubt.
    /// </summary>
    [Fact]
    public void ConfidenceRisesWithEvidenceOnAKnownDefender()
    {
        Assert.Equal(MobAttackConfidence.Low, GradeOf(hits: 10, level: 55));
        Assert.Equal(MobAttackConfidence.Medium, GradeOf(hits: 50, level: 55));
        Assert.Equal(MobAttackConfidence.High, GradeOf(hits: 250, level: 55));
    }

    /// <summary>
    /// Volume cannot fix not knowing who was standing there: a thousand hits
    /// pooled across unknown levels is a thousand hits describing nobody, so an
    /// unknown level caps the grade at Medium however much evidence there is.
    /// </summary>
    [Fact]
    public void UnknownDefenderLevelCapsConfidenceAtMedium()
    {
        Assert.Equal(MobAttackConfidence.Low, GradeOf(hits: 10, level: null));
        Assert.Equal(MobAttackConfidence.Medium, GradeOf(hits: 250, level: null));
        Assert.Equal(MobAttackConfidence.Medium, GradeOf(hits: 5000, level: null));
    }

    private static MobAttackConfidence GradeOf(int hits, int? level)
    {
        var index = new MobAttackIndex();
        index.Add([Sample(Melee([.. Enumerable.Repeat(100L, hits)]), level: level)]);
        return Assert.Single(index.Estimates()).Confidence;
    }

    /// <summary>
    /// Most recently fought first, not best-evidenced first. A server's index
    /// accumulates for months, so ranking by evidence would bury tonight's camp
    /// under every zone the account has ever worked — and bury it deeper the
    /// longer the app is used.
    /// </summary>
    [Fact]
    public void MostRecentlyFoughtComesFirst()
    {
        var index = new MobAttackIndex();

        // A camp worked to death last week: hundreds of swings, High confidence.
        index.Add(Enumerable.Range(0, 40).Select(i => Named(
            "An old favourite", Melee([.. Enumerable.Repeat(100L, 10)]), minute: i)));

        // Tonight's mob, one fight in: thin evidence, and the row that matters.
        index.Add([Named("Tonight's problem", Melee(300), minute: 10_000)]);

        var estimates = index.Estimates();
        Assert.Equal("Tonight's problem", estimates[0].Mob);
        Assert.Equal(MobAttackConfidence.Low, estimates[0].Confidence);
        Assert.Equal("An old favourite", estimates[1].Mob);
        Assert.Equal(MobAttackConfidence.High, estimates[1].Confidence);
    }

    private static AttackSample Named(string mob, SkillTally tally, int minute) =>
        new(mob, "The Ruins of Old Guk", 3, "Fused", 55, "Kazint",
            T0.AddMinutes(minute), T0.AddMinutes(minute).AddSeconds(45),
            new Dictionary<string, SkillTally> { ["Crushes"] = tally });

    /// <summary>
    /// Round-tripped through the JSON the store actually writes, an index
    /// reports exactly what it did before — the sparse histogram included,
    /// which is the part with a shape a serializer could plausibly lose.
    /// </summary>
    [Fact]
    public void SnapshotRoundTripsThroughJson()
    {
        var index = new MobAttackIndex();
        index.Add([Sample(Melee(100, 200, 300)), Sample(Melee(400), minute: 5)]);
        var before = Assert.Single(index.Estimates());

        var reloaded = new MobAttackIndex();
        reloaded.Load(JsonSerializer.Deserialize<List<MobAttackRecord>>(
            JsonSerializer.Serialize(index.Snapshot()))!);

        var after = Assert.Single(reloaded.Estimates());
        Assert.Equal(before with { Defenders = [], Skills = [] }, after with { Defenders = [], Skills = [] });
        Assert.Equal(before.Defenders, after.Defenders);
        Assert.Equal(before.Skills, after.Skills);

        // And it still recognizes the fights it already counted.
        Assert.Equal(0, reloaded.Add([Sample(Melee(100, 200, 300))]));
    }
}

public class MobAttackHarvestTests
{
    private static readonly DateTime T0 = new(2026, 8, 8, 20, 0, 0);

    /// <summary>
    /// A closed fight against a zoned mob yields one sample per defender, with
    /// only that mob's swings in it.
    /// </summary>
    [Fact]
    public void HarvestsOneSamplePerDefenderFromAClosedFight()
    {
        var (records, fights, identity) = Scene();
        var samples = MobAttackIndex.Harvest(
            records, fights.Fights, identity, DefenderLevels.Build(records, "Kazint"));

        var sample = Assert.Single(samples, s => s.Defender == "Kazint");
        Assert.Equal("A dar ghoul knight", sample.Mob);
        Assert.Equal("The Ruins of Old Guk", sample.Zone);
        Assert.Equal(3, sample.Difficulty);
        Assert.Equal(55, sample.DefenderLevel);

        var crushes = sample.BySkill["Crushes"];
        Assert.Equal(3, crushes.Swings);
        Assert.Equal(2, crushes.Landed);
        Assert.Equal(1, crushes.Misses);
        Assert.Equal(300, crushes.Total);
    }

    /// <summary>
    /// The players' own damage is not incoming damage, however it is measured.
    /// </summary>
    [Fact]
    public void OutgoingDamageIsNotHarvested()
    {
        var (records, fights, identity) = Scene();
        var samples = MobAttackIndex.Harvest(
            records, fights.Fights, identity, DefenderLevels.Build(records, "Kazint"));

        Assert.DoesNotContain(samples, s => s.Defender == "A dar ghoul knight");
        Assert.All(samples, s => Assert.DoesNotContain("Slashes", s.BySkill.Keys));
    }

    /// <summary>
    /// A fight still in progress is left alone. Harvesting it would bank its
    /// opening seconds and then reject the finished version as a duplicate,
    /// which is worse than waiting a second.
    /// </summary>
    [Fact]
    public void OpenFightIsNotHarvested()
    {
        var (records, fights, identity) = Scene(close: false);

        Assert.Empty(MobAttackIndex.Harvest(
            records, fights.Fights, identity, DefenderLevels.Build(records, "Kazint")));
    }

    /// <summary>
    /// A fight with no zone is dropped rather than pooled, for the reason F25
    /// drops one: pooling it mixes difficulties, and a difficulty rescales
    /// everything about the mob.
    /// </summary>
    [Fact]
    public void FightWithNoZoneIsDropped()
    {
        var (records, fights, identity) = Scene(zone: null);

        Assert.Empty(MobAttackIndex.Harvest(
            records, fights.Fights, identity, DefenderLevels.Build(records, "Kazint")));
    }

    /// <summary>
    /// A group member who never says a word, joins a raid or turns up in a /who
    /// is still someone being hit. Demanding verification would drop most of the
    /// party, so the test is the same one the tanking source uses: not an NPC.
    /// </summary>
    [Fact]
    public void UnverifiedGroupMemberIsStillADefender()
    {
        var (records, fights, identity) = Scene();
        var samples = MobAttackIndex.Harvest(
            records, fights.Fights, identity, DefenderLevels.Build(records, "Kazint"));

        Assert.False(identity.IsVerifiedPlayer("Vandil"));
        var vandil = Assert.Single(samples, s => s.Defender == "Vandil");
        Assert.Equal(500, vandil.BySkill["Crushes"].Total);
    }

    /// <summary>Pets are on the players' side and take hits like anyone else.</summary>
    [Fact]
    public void PetsAreHarvestedAsDefenders()
    {
        var (records, fights, identity) = Scene();
        var samples = MobAttackIndex.Harvest(
            records, fights.Fights, identity, DefenderLevels.Build(records, "Kazint"));

        var pet = Assert.Single(samples, s => s.Defender == "Gybtor");
        Assert.Null(pet.DefenderLevel); // nothing ever levelled a pet
        Assert.Equal(80, pet.BySkill["Bites"].Total);
    }

    /// <summary>
    /// Builds a fight the tracker itself produced, so the harvest is exercised
    /// against real fight boundaries rather than a hand-made Fight.
    /// </summary>
    private static (RecordStore Records, FightTracker Fights, IdentityRegistry Identity) Scene(
        bool close = true, string? zone = "The Ruins of Old Guk 3 (Fused)")
    {
        var identity = new IdentityRegistry();
        identity.AddVerifiedPlayer("Kazint");
        identity.MapPetToOwner("Gybtor", "Kazint");

        var records = new RecordStore();
        var fights = new FightTracker(identity);
        var at = T0;

        void Emit(GameEvent evt, int seconds = 1)
        {
            at = at.AddSeconds(seconds);
            records.Append(at, evt);
            fights.Process(at, evt);
        }

        records.Append(T0, new WhoEvent("Kazint", 55, "Warrior"));
        if (zone is not null)
        {
            Emit(new ZoneEvent(zone), seconds: 0);
        }

        Emit(new DamageEvent("Kazint", "A dar ghoul knight", 250, DamageKind.Melee, "Slashes"));
        Emit(new DamageEvent("A dar ghoul knight", "Kazint", 100, DamageKind.Melee, "Crushes"));
        Emit(new DamageEvent("A dar ghoul knight", "Kazint", 200, DamageKind.Melee, "Crushes"));
        Emit(new DamageEvent("A dar ghoul knight", "Kazint", 0, DamageKind.Miss, "Crushes"));
        Emit(new DamageEvent("A dar ghoul knight", "Gybtor", 80, DamageKind.Melee, "Bites"));
        Emit(new DamageEvent("A dar ghoul knight", "Vandil", 500, DamageKind.Melee, "Crushes"));

        if (close)
        {
            Emit(new DeathEvent("A dar ghoul knight", "Kazint"));
            fights.ExpireFights(at.AddHours(1));
        }

        return (records, fights, identity);
    }
}

public class IncomingHitsTests
{
    private static readonly DateTime T0 = new(2026, 8, 8, 20, 0, 0);

    /// <summary>
    /// The feed is the ordering, which is the one thing an aggregation cannot
    /// keep — so it comes back oldest-first, exactly as the log wrote it.
    /// </summary>
    [Fact]
    public void ReturnsIncomingSwingsInLogOrder()
    {
        var (records, fights, identity) = Scene();
        var result = IncomingHitsBuilder.Build(records, fights, identity, new QueryScope());

        Assert.Equal(
            ["Crushes", "Crushes", "Crushes", "Bites", "Crushes"],
            result.Hits.Select(h => h.Skill));
        Assert.Equal([100L, 200L, 0L, 80L, 500L], result.Hits.Select(h => h.Amount));
        Assert.Equal(DamageKind.Miss, result.Hits[2].Outcome);
    }

    /// <summary>An avoided swing has no number, and leaving it out would be the story minus its half.</summary>
    [Fact]
    public void AvoidedSwingsAreKept()
    {
        var (records, fights, identity) = Scene();
        var result = IncomingHitsBuilder.Build(records, fights, identity, new QueryScope());

        Assert.Contains(result.Hits, h => h.Outcome == DamageKind.Miss && h.Amount == 0);
    }

    [Fact]
    public void OutgoingDamageIsNeverInTheFeed()
    {
        var (records, fights, identity) = Scene();
        var result = IncomingHitsBuilder.Build(records, fights, identity, new QueryScope());

        Assert.DoesNotContain(result.Hits, h => h.Attacker == "Kazint");
        Assert.All(result.Hits, h => Assert.Equal("A dar ghoul knight", h.Attacker));
    }

    /// <summary>A pet's hits carry the owner, so a feed can roll them up.</summary>
    [Fact]
    public void PetHitsCarryTheirOwner()
    {
        var (records, fights, identity) = Scene();
        var result = IncomingHitsBuilder.Build(records, fights, identity, new QueryScope());

        var pet = Assert.Single(result.Hits, h => h.Defender == "Gybtor");
        Assert.Equal("Kazint", pet.DefenderOwner);
    }

    [Fact]
    public void DefenderFilterNarrowsTheFeed()
    {
        var (records, fights, identity) = Scene();
        var result = IncomingHitsBuilder.Build(
            records, fights, identity, new QueryScope(), defenders: ["Kazint"]);

        // The pet resolves through its owner, so filtering on the owner keeps
        // it — and the other player in the fight drops out.
        Assert.Equal(4, result.Hits.Count);
        Assert.Contains(result.Hits, h => h.Defender == "Gybtor");
        Assert.DoesNotContain(result.Hits, h => h.Defender == "Vandil");
    }

    /// <summary>
    /// The tail is the last N, and the count says what was left out — a feed
    /// that silently truncated would read as "this is all of it".
    /// </summary>
    [Fact]
    public void LimitKeepsTheNewestAndReportsTheTotal()
    {
        var (records, fights, identity) = Scene();
        var result = IncomingHitsBuilder.Build(
            records, fights, identity, new QueryScope(), limit: 2);

        Assert.Equal(5, result.Total);
        Assert.Equal(2, result.Hits.Count);
        Assert.Equal([80L, 500L], result.Hits.Select(h => h.Amount));
    }

    private static (RecordStore Records, FightTracker Fights, IdentityRegistry Identity) Scene()
    {
        var identity = new IdentityRegistry();
        identity.AddVerifiedPlayer("Kazint");
        identity.MapPetToOwner("Gybtor", "Kazint");

        var records = new RecordStore();
        var fights = new FightTracker(identity);
        var at = T0;

        void Emit(GameEvent evt)
        {
            at = at.AddSeconds(1);
            records.Append(at, evt);
            fights.Process(at, evt);
        }

        Emit(new DamageEvent("Kazint", "A dar ghoul knight", 250, DamageKind.Melee, "Slashes"));
        Emit(new DamageEvent("A dar ghoul knight", "Kazint", 100, DamageKind.Melee, "Crushes"));
        Emit(new DamageEvent("A dar ghoul knight", "Kazint", 200, DamageKind.Melee, "Crushes"));
        Emit(new DamageEvent("A dar ghoul knight", "Kazint", 0, DamageKind.Miss, "Crushes"));
        Emit(new DamageEvent("A dar ghoul knight", "Gybtor", 80, DamageKind.Melee, "Bites"));
        Emit(new DamageEvent("A dar ghoul knight", "Vandil", 500, DamageKind.Melee, "Crushes"));

        return (records, fights, identity);
    }
}
