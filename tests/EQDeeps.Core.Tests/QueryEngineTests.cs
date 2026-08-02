using EQDeeps.Core.Events;
using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// Query-engine verification against hand-computed metric values (HANDOFF
/// phase-4 exit criterion). One fixed scenario, computed on paper:
///
/// Fight "An ice giant", t2..t8 (7 s inclusive):
///   Raider01: melee 100 @t2, 200 Crit @t3, 300 @t8, DS 25 @t7
///     → total 625, hits 4, crit 1/4, active [t2..t8] = 7 s
///   Raider02: kick 50 @t5, DD fire 50 @t6 → total 100, active [t5..t6] = 2 s
///   Giant → Raider01 melee 150 @t4; → Raider02 dodge @t7
///   Heal inside fight: Raider02 → Raider01 400 (500) @t6
///   Raid time = union of segments = [t2..t8] = 7 s.
/// </summary>
public class QueryEngineTests
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);

    private readonly IdentityRegistry _identity = new();
    private readonly RecordStore _records = new();
    private readonly FightTracker _tracker;
    private readonly QueryEngine _engine;

    public QueryEngineTests()
    {
        _identity.AddVerifiedPlayer("Raider01");
        _identity.AddVerifiedPlayer("Raider02");
        _tracker = new FightTracker(_identity);
        _engine = new QueryEngine(_records, _tracker, _identity, "Kizant");

        Add(2, new DamageEvent("Raider01", "An ice giant", 100, DamageKind.Melee, "Crushes"));
        Add(3, new DamageEvent("Raider01", "An ice giant", 200, DamageKind.Melee, "Crushes", HitModifiers.Critical));
        Add(4, new DamageEvent("An ice giant", "Raider01", 150, DamageKind.Melee, "Hits"));
        Add(5, new DamageEvent("Raider02", "An ice giant", 50, DamageKind.Melee, "Kicks"));
        Add(6, new DamageEvent("Raider02", "An ice giant", 50, DamageKind.DirectDamage, "Burst of Flames", School: "fire"));
        Add(6, new HealEvent("Raider02", "Raider01", 400, 500, OverTime: false, "Spirit of the Wood XXXIV"));
        Add(7, new DamageEvent("An ice giant", "Raider02", 0, DamageKind.Dodge, "Hits"));
        Add(7, new DamageEvent("Raider01", "An ice giant", 25, DamageKind.DamageShield, null));
        Add(8, new DamageEvent("Raider01", "An ice giant", 300, DamageKind.Melee, "Crushes"));
        Add(8, new DeathEvent("An ice giant", "Raider01"));
    }

    private void Add(int t, GameEvent evt)
    {
        var timestamp = T0.AddSeconds(t);
        _records.Append(timestamp, evt);
        _tracker.Process(timestamp, evt);
    }

    private static QueryRow Row(QueryResult result, string key) =>
        result.Rows.Single(r => r.Key == key);

    // ---- damage summary: hand-computed ------------------------------------

    [Fact]
    public void DamageSummaryMatchesHandComputedValues()
    {
        var result = _engine.Execute(CannedQueries.DamageSummary());

        Assert.Equal(7, result.RaidSeconds);
        Assert.Equal(725, result.Totals["total"]);

        var r1 = Row(result, "Raider01");
        Assert.Equal(625, r1.Metrics["total"]);
        Assert.Equal(4, r1.Metrics["hits"]);
        Assert.Equal(7, r1.Metrics["activeSeconds"]);
        Assert.Equal(625.0 / 7, r1.Metrics["dps"], precision: 10);
        Assert.Equal(625.0 / 7, r1.Metrics["sdps"], precision: 10);
        Assert.Equal(25.0, r1.Metrics["critRate"], precision: 10);       // 1 of 4
        Assert.Equal(625.0 / 725 * 100, r1.Metrics["percentOfTotal"], precision: 10);
        Assert.Equal(300, r1.Metrics["maxHit"]);

        var r2 = Row(result, "Raider02");
        Assert.Equal(100, r2.Metrics["total"]);
        Assert.Equal(2, r2.Metrics["activeSeconds"]);
        Assert.Equal(50.0, r2.Metrics["dps"], precision: 10);            // 100 / 2
        Assert.Equal(100.0 / 7, r2.Metrics["sdps"], precision: 10);

        // Ranked by total: Raider01 first.
        Assert.Equal(["Raider01", "Raider02"], result.Rows.Select(r => r.Key));

        // Drill-down: player → spell/skill.
        var crushes = r1.Children!.Single(c => c.Key == "Crushes");
        Assert.Equal(600, crushes.Metrics["total"]);
        Assert.Equal(3, crushes.Metrics["hits"]);
    }

    [Fact]
    public void AvgCritExcludesLuckyHits()
    {
        // Add a lucky crit for Raider01 in a fresh second: avgCrit must use only
        // the non-lucky crit (200), while critRate counts both.
        Add(9, new DamageEvent("Raider01", "An ice giant", 1000, DamageKind.Melee, "Crushes",
            HitModifiers.Critical | HitModifiers.Lucky));

        var result = _engine.Execute(new QuerySpec { Metrics = ["avgCrit", "avgLucky", "critRate", "luckyRate"] });
        var r1 = Row(result, "Raider01");
        Assert.Equal(200, r1.Metrics["avgCrit"]);
        Assert.Equal(1000, r1.Metrics["avgLucky"]);
        Assert.Equal(2.0 / 5 * 100, r1.Metrics["critRate"], precision: 10);
        Assert.Equal(50, r1.Metrics["luckyRate"]); // 1 lucky of 2 crits
    }

    // ---- tanking summary ---------------------------------------------------

    [Fact]
    public void TankingSummaryMatchesHandComputedValues()
    {
        var result = _engine.Execute(CannedQueries.TankingSummary());

        Assert.Equal(150, result.Totals["total"]);

        var r1 = Row(result, "Raider01");
        Assert.Equal(150, r1.Metrics["total"]);
        Assert.Equal(1, r1.Metrics["meleeAttempts"]);
        Assert.Equal(100.0, r1.Metrics["undefendedRate"], precision: 10);

        var r2 = Row(result, "Raider02");
        Assert.Equal(0, r2.Metrics["total"]);
        Assert.Equal(1, r2.Metrics["meleeAttempts"]);
        Assert.Equal(0.0, r2.Metrics["undefendedRate"], precision: 10); // the dodge
    }

    // ---- healing summary ---------------------------------------------------

    [Fact]
    public void HealingSummaryTracksOverheal()
    {
        var result = _engine.Execute(CannedQueries.HealingSummary());

        var r2 = Row(result, "Raider02");
        Assert.Equal(400, r2.Metrics["total"]);
        Assert.Equal(100, r2.Metrics["extra"]);
        Assert.Equal(500, r2.Metrics["potential"]);
        Assert.Equal(20.0, r2.Metrics["overhealRate"], precision: 10); // 100 / 500
    }

    // ---- experience: hand-computed ----------------------------------------

    [Fact]
    public void ExperienceMetricsMatchHandComputedValues()
    {
        // XP at t10..t70: solo 5% @t10, party 1.5% @t40, classic no-percent
        // solo gain @t70, AA point @t70. Active span t10..t70 = 61 s inclusive.
        Add(10, new ExperienceEvent(5.0, Party: false));
        Add(40, new ExperienceEvent(1.5, Party: true));
        Add(70, new ExperienceEvent(null, Party: false));
        Add(70, new ExperienceEvent(null, Party: false, AaPoint: true, AaTotal: 2));

        var result = _engine.Execute(new QuerySpec
        {
            Source = QuerySource.Experience,
            Scope = new QueryScope { TimeRanges = [new TimeRange(T0, T0.AddSeconds(100))] },
            GroupBy = [Dimension.Spell],
        });

        Assert.Equal(6.5, result.Totals["xpPercent"], precision: 10);
        Assert.Equal(3, result.Totals["xpGains"]);
        Assert.Equal(1, result.Totals["aaPoints"]);
        Assert.Equal(61, result.RaidSeconds);
        Assert.Equal(6.5 * 3600 / 61, result.Totals["xpPerHour"], precision: 8);

        var solo = Row(result, "solo");
        Assert.Equal(5.0, solo.Metrics["xpPercent"], precision: 10);
        Assert.Equal(2, solo.Metrics["xpGains"]); // the classic gain counts, adds 0%

        var party = Row(result, "party");
        Assert.Equal(1.5, party.Metrics["xpPercent"], precision: 10);
        Assert.Equal(1, party.Metrics["xpGains"]);

        var aa = Row(result, "AA point");
        Assert.Equal(1, aa.Metrics["aaPoints"]);
        Assert.Equal(0, aa.Metrics["xpGains"]);

        // An unrestricted scope must cover the whole record stream — XP that
        // falls outside every fight (here: everything past t8) still counts.
        var unrestricted = _engine.Execute(new QuerySpec { Source = QuerySource.Experience });
        Assert.Equal(6.5, unrestricted.Totals["xpPercent"], precision: 10);
        Assert.Equal(3, unrestricted.Totals["xpGains"]);
        Assert.Equal(1, unrestricted.Totals["aaPoints"]);
    }

    // ---- faction: hand-computed -------------------------------------------

    [Fact]
    public void FactionMetricsMatchHandComputedValues()
    {
        Add(10, new FactionEvent("Frogloks of Guk", -4, Better: false));
        Add(11, new FactionEvent("Frogloks of Guk", -4, Better: false));
        Add(12, new FactionEvent("Guards of Qeynos", 2, Better: true));
        Add(13, new FactionEvent("Guards of Qeynos", null, Better: true));  // classic → +1
        Add(14, new FactionEvent("Bloodsabers", null, Better: false, Capped: true)); // no movement

        var result = _engine.Execute(new QuerySpec { Source = QuerySource.Faction });

        Assert.Equal(-5, result.Totals["factionNet"]); // -8 + 2 + 1
        Assert.Equal(2, result.Totals["factionUps"]);
        Assert.Equal(2, result.Totals["factionDowns"]);
        Assert.Equal(1, result.Totals["factionCapped"]);

        var guk = Row(result, "Frogloks of Guk");
        Assert.Equal(-8, guk.Metrics["factionNet"]);
        Assert.Equal(2, guk.Metrics["factionDowns"]);

        var qeynos = Row(result, "Guards of Qeynos");
        Assert.Equal(3, qeynos.Metrics["factionNet"]);
        Assert.Equal(2, qeynos.Metrics["factionUps"]);

        var bloodsabers = Row(result, "Bloodsabers");
        Assert.Equal(0, bloodsabers.Metrics["factionNet"]);
        Assert.Equal(1, bloodsabers.Metrics["factionCapped"]);
    }

    // ---- loot: hand-computed ----------------------------------------------

    [Fact]
    public void LootMetricsMatchHandComputedValues()
    {
        Add(20, new LootEvent("Kizant", "Froglok Meat", "a froglok ton knight", Copper: 5));
        Add(21, new LootEvent("Kizant", "Phosphorous Powder", "a dar ghoul knight", Copper: 1234, Quantity: 2));
        Add(22, new LootEvent("Kizant", "Cold-Forged Cudgel", "Queen Dracnia"));
        Add(23, new LootEvent("Kizant", null, "corpse", Copper: 1253));

        var result = _engine.Execute(new QuerySpec
        {
            Source = QuerySource.Loot,
            GroupBy = [Dimension.Spell],
        });

        Assert.Equal(4, result.Totals["loots"]); // 1 + 2 + 1; coin-only adds none
        Assert.Equal(2.492, result.Totals["platinum"], precision: 10); // (5+1234+1253)/1000

        var powder = Row(result, "Phosphorous Powder");
        Assert.Equal(2, powder.Metrics["loots"]);
        Assert.Equal(1.234, powder.Metrics["platinum"], precision: 10);

        var coin = Row(result, "coin");
        Assert.Equal(0, coin.Metrics["loots"]);
        Assert.Equal(1.253, coin.Metrics["platinum"], precision: 10);

        // Rows rank by the first requested metric (loots), not damage total.
        Assert.Equal("Phosphorous Powder", result.Rows[0].Key);
    }

    // ---- considers: hand-computed -----------------------------------------

    [Fact]
    public void ConsiderMetricsMatchHandComputedValues()
    {
        Add(30, new ConsiderEvent("A dar ghoul knight", "scowl", 22));
        Add(31, new ConsiderEvent("A dar ghoul knight", "scowl", 24)); // respawn, higher level
        Add(32, new ConsiderEvent("A bat", "indifferent", 7));
        Add(33, new ConsiderEvent("a skeleton", "threatening", null)); // classic, no level

        var result = _engine.Execute(new QuerySpec
        {
            Source = QuerySource.Considers,
            GroupBy = [Dimension.Player, Dimension.Spell],
        });

        Assert.Equal(4, result.Totals["considers"]);

        var knight = Row(result, "A dar ghoul knight");
        Assert.Equal(2, knight.Metrics["considers"]);
        Assert.Equal(24, knight.Metrics["conLevel"]); // max level seen
        Assert.Equal(2, knight.Children!.Single(c => c.Key == "scowl").Metrics["considers"]);

        var skeleton = Row(result, "a skeleton");
        Assert.Equal(0, skeleton.Metrics["conLevel"]); // classic: level unknown

        // Ranked by considers (first metric): the twice-conned knight first.
        Assert.Equal("A dar ghoul knight", result.Rows[0].Key);
    }

    // ---- validity toggles are filters, never reparses ----------------------

    [Fact]
    public void DamageShieldToggleFiltersWithoutReparse()
    {
        var versionBefore = _records.Version;

        var without = _engine.Execute(new QuerySpec
        {
            Filters = [new QueryFilter { Flag = ValidityFlag.DamageShield, Exclude = true }],
        });

        var r1 = Row(without, "Raider01");
        Assert.Equal(600, r1.Metrics["total"]);
        Assert.Equal(3, r1.Metrics["hits"]);
        Assert.Equal(600.0 / 700 * 100, r1.Metrics["percentOfTotal"], precision: 10);
        Assert.Equal(100.0 / 3, r1.Metrics["critRate"], precision: 10);

        Assert.Equal(versionBefore, _records.Version); // nothing reparsed or mutated

        // Only-DS view (exclude: false keeps matches).
        var only = _engine.Execute(new QuerySpec
        {
            Filters = [new QueryFilter { Flag = ValidityFlag.DamageShield }],
        });
        Assert.Equal(25, Row(only, "Raider01").Metrics["total"]);
    }

    [Fact]
    public void DimensionFilterRestrictsRows()
    {
        var result = _engine.Execute(new QuerySpec
        {
            Filters = [new QueryFilter { Dim = Dimension.Player, Values = ["Raider02"] }],
        });

        var row = Assert.Single(result.Rows);
        Assert.Equal("Raider02", row.Key);
        Assert.Equal(100, result.Totals["total"]);
    }

    // ---- grouping and dimensions ------------------------------------------

    [Fact]
    public void GroupByDamageTypeUsesSchools()
    {
        var result = _engine.Execute(new QuerySpec { GroupBy = [Dimension.DamageType] });

        Assert.Equal(650, Row(result, "melee").Metrics["total"]);
        Assert.Equal(50, Row(result, "fire").Metrics["total"]);
        Assert.Equal(25, Row(result, "damageShield").Metrics["total"]);
    }

    // ---- pet rollup --------------------------------------------------------

    [Fact]
    public void PetRollupMergesOwnersAndStaysDrillable()
    {
        _identity.MapPetToOwner("Xobatik", "Raider02");
        Add(9, new DamageEvent("Xobatik", "An ice giant", 60, DamageKind.Melee, "Bites"));

        var result = _engine.Execute(new QuerySpec());
        var merged = Row(result, "Raider02");
        Assert.Equal("Raider02 +Pets", merged.Label);
        Assert.Equal(160, merged.Metrics["total"]);

        var actors = merged.Children!;
        Assert.Equal(100, actors.Single(a => a.Key == "Raider02").Metrics["total"]);
        Assert.Equal(60, actors.Single(a => a.Key == "Xobatik").Metrics["total"]);

        var split = _engine.Execute(new QuerySpec { PetRollup = false });
        Assert.Equal(60, Row(split, "Xobatik").Metrics["total"]);
        Assert.Equal(100, Row(split, "Raider02").Metrics["total"]);
        Assert.Equal("Raider02", Row(split, "Raider02").Label);
    }

    [Fact]
    public void PlayerFilterFollowsPetRollup()
    {
        _identity.MapPetToOwner("Xobatik", "Raider02");
        Add(9, new DamageEvent("Xobatik", "An ice giant", 60, DamageKind.Melee, "Bites"));

        // Rollup on: "Raider02" means owner + pets.
        var rolled = _engine.Execute(new QuerySpec
        {
            Filters = [new QueryFilter { Dim = Dimension.Player, Values = ["Raider02"] }],
        });
        Assert.Equal(160, rolled.Totals["total"]);

        // Rollup off: raw actor names only.
        var raw = _engine.Execute(new QuerySpec
        {
            PetRollup = false,
            Filters = [new QueryFilter { Dim = Dimension.Player, Values = ["Raider02"] }],
        });
        Assert.Equal(100, raw.Totals["total"]);
    }

    // ---- scope: fights, trim, buckets --------------------------------------

    [Fact]
    public void FightSelectionScopesToThatFightsNpc()
    {
        // Second fight against another NPC later.
        Add(300, new DamageEvent("Raider01", "A shadow drake", 400, DamageKind.Melee, "Crushes"));

        var all = _engine.Execute(new QuerySpec());
        Assert.Equal(1125, all.Totals["total"]);

        var firstOnly = _engine.Execute(new QuerySpec
        {
            Scope = new QueryScope { FightIds = [_tracker.Fights[0].Id] },
        });
        Assert.Equal(725, firstOnly.Totals["total"]);

        var secondOnly = _engine.Execute(new QuerySpec
        {
            Scope = new QueryScope { FightIds = [_tracker.Fights[1].Id] },
        });
        Assert.Equal(400, secondOnly.Totals["total"]);
        Assert.Equal(1, secondOnly.RaidSeconds);
    }

    [Fact]
    public void LastSecondsScopeIsFightAgnostic()
    {
        // Trailing window anchored to the newest record (t8): lastSeconds=3
        // covers [t6..t8] — DD 50 + heal + dodge + DS 25 + melee 300 + death.
        var result = _engine.Execute(new QuerySpec
        {
            Scope = new QueryScope { LastSeconds = 3 },
            Metrics = ["total", "dps", "hits"],
        });

        Assert.Equal(375, result.Totals["total"]);
        Assert.Equal(325, Row(result, "Raider01").Metrics["total"]);
        Assert.Equal(50, Row(result, "Raider02").Metrics["total"]);
        Assert.Equal(3, result.RaidSeconds);

        // Damage against an unclassified single-word mob still counts — the
        // fight tracker's NPC assumption applies to raw ranges too.
        Add(9, new DamageEvent("Raider01", "Swarmling", 40, DamageKind.Melee, "Crushes"));
        var withUnknown = _engine.Execute(new QuerySpec
        {
            Scope = new QueryScope { LastSeconds = 2 },
            Metrics = ["total"],
        });
        Assert.Equal(340, Row(withUnknown, "Raider01").Metrics["total"]); // 300 @t8 + 40 @t9

        // Player-on-player damage stays out.
        Add(10, new DamageEvent("Raider01", "Raider02", 999, DamageKind.DamageShield, null));
        var pvp = _engine.Execute(new QuerySpec
        {
            Scope = new QueryScope { LastSeconds = 1 },
            Metrics = ["total"],
        });
        Assert.Empty(pvp.Rows);
    }

    [Fact]
    public void TrimNarrowsTheSelectionTimeline()
    {
        // Fight window [t2..t8]; skip the first 3 s → [t5..t8].
        var result = _engine.Execute(new QuerySpec
        {
            Scope = new QueryScope { SkipFirstSeconds = 3 },
        });

        var r1 = Row(result, "Raider01");
        Assert.Equal(325, r1.Metrics["total"]);       // DS 25 @t7 + 300 @t8
        Assert.Equal(2, r1.Metrics["activeSeconds"]); // [t7..t8]

        var capped = _engine.Execute(new QuerySpec
        {
            Scope = new QueryScope { MaxSeconds = 2 },  // [t2..t3]
        });
        Assert.Equal(300, Row(capped, "Raider01").Metrics["total"]);
    }

    [Fact]
    public void BucketedQueryEmitsPerSecondSeries()
    {
        var result = _engine.Execute(new QuerySpec { BucketSeconds = 1 });
        var series = Row(result, "Raider01").Series!;

        Assert.Equal(
            [(T0.AddSeconds(2), 100.0), (T0.AddSeconds(3), 200.0), (T0.AddSeconds(7), 25.0), (T0.AddSeconds(8), 300.0)],
            series.Select(p => (p.BucketStart, p.Value)));
    }

    // ---- caching and serialization -----------------------------------------

    [Fact]
    public void ResultsAreCachedUntilDataChanges()
    {
        var spec = new QuerySpec();
        var first = _engine.Execute(spec);
        Assert.Same(first, _engine.Execute(spec));

        Add(9, new DamageEvent("Raider01", "An ice giant", 1, DamageKind.Melee, "Crushes"));
        var third = _engine.Execute(spec);
        Assert.NotSame(first, third);
        Assert.Equal(726, third.Totals["total"]);
    }

    [Fact]
    public void QuerySpecRoundTripsThroughJson()
    {
        var spec = new QuerySpec
        {
            Source = QuerySource.Damage,
            Scope = new QueryScope { FightIds = [1, 2], SkipFirstSeconds = 10, MaxSeconds = 60 },
            GroupBy = [Dimension.Player, Dimension.Spell],
            Metrics = ["total", "sdps", "critRate"],
            Filters = [new QueryFilter { Flag = ValidityFlag.DamageShield, Exclude = true }],
            BucketSeconds = 6,
            PetRollup = false,
        };

        var json = QuerySpecJson.Serialize(spec);
        Assert.Contains("\"damageShield\"", json); // camelCase enums for the UI
        var back = QuerySpecJson.Deserialize(json)!;

        Assert.Equal(spec.Source, back.Source);
        Assert.Equal(spec.Scope.FightIds, back.Scope.FightIds);
        Assert.Equal(spec.Scope.MaxSeconds, back.Scope.MaxSeconds);
        Assert.Equal(spec.GroupBy, back.GroupBy);
        Assert.Equal(spec.Metrics, back.Metrics);
        Assert.Equal(ValidityFlag.DamageShield, back.Filters[0].Flag);
        Assert.True(back.Filters[0].Exclude);
        Assert.Equal(6, back.BucketSeconds);
        Assert.False(back.PetRollup);
    }
}
