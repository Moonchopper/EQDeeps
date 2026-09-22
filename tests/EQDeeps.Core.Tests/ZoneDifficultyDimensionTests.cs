using EQDeeps.Core.Events;
using EQDeeps.Core.Ingestion;
using EQDeeps.Core.Parsing;
using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;
using EQDeeps.TestSupport;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// C2: the one label property (<see cref="InstanceZone.DifficultyLabel"/>) and
/// <see cref="InstanceZone.Display"/> re-derived from it. The four forms named
/// in the brief, plus the edge cases <c>Display</c> already had to handle.
/// </summary>
public class InstanceZoneDifficultyLabelTests
{
    [Theory]
    [InlineData("The Estate of Unrest 4 (Refined)", "4 (Refined)")]
    [InlineData("The City of Guk 1 (Awakened)", "1 (Awakened)")]
    [InlineData("The Plane of Fear - Group 3 (Fused)", "Group 3 (Fused)")]
    [InlineData("The Permafrost Caverns - Solo 1 (Awakened)", "Solo 1 (Awakened)")]
    [InlineData("Nagafen's Lair - Solo", "Solo")]
    [InlineData("Nagafen's Lair - Group", "Group")]
    [InlineData("The Estate of Unrest", InstanceZone.OpenWorld)]
    [InlineData("Butcherblock Mountains", InstanceZone.OpenWorld)]
    public void ProducesTheLoggedDifficultyLabel(string logged, string expectedLabel)
    {
        Assert.Equal(expectedLabel, InstanceZone.Parse(logged).DifficultyLabel);
    }

    /// <summary>
    /// Display must stay byte-identical to what it produced before this
    /// change — re-derived from BaseName + DifficultyLabel now, not from
    /// KeyName + a hand-appended tier suffix, but the same four shapes must
    /// come out unchanged.
    /// </summary>
    [Theory]
    [InlineData("The Estate of Unrest 4 (Refined)")]
    [InlineData("The Plane of Fear - Group 3 (Fused)")]
    [InlineData("Nagafen's Lair - Solo")]
    [InlineData("The Estate of Unrest")]
    [InlineData("Nagafen's Lair 1 (Awakened)")]
    public void DisplayStaysByteIdenticalToTheLoggedString(string logged)
    {
        Assert.Equal(logged, InstanceZone.Parse(logged).Display);
    }

    /// <summary>
    /// Mirrors Display's own condition for when a tier counts (Difficulty AND
    /// a non-empty TierName) so a struct built by hand with one but not the
    /// other cannot disagree between DifficultyLabel and Display.
    /// </summary>
    [Fact]
    public void ATierNumberWithNoTierWordDoesNotCountAsATierInEitherProperty()
    {
        var zone = new InstanceZone("Some Zone", 4, null);

        Assert.Equal(InstanceZone.OpenWorld, zone.DifficultyLabel);
        Assert.Equal("Some Zone", zone.Display);
    }

    [Fact]
    public void ATierNumberWithNoTierWordButAModeStillShowsTheMode()
    {
        var zone = new InstanceZone("Some Zone", 4, null, "Solo");

        Assert.Equal("Solo", zone.DifficultyLabel);
        Assert.Equal("Some Zone - Solo", zone.Display);
    }
}

/// <summary>
/// C3: the index-based zone step function. Chosen over <see cref="ContextTimeline"/>'s
/// time-keyed spans because log resolution is one second and a zone's first
/// lines routinely land in the same second as its "You have entered" line —
/// which a time lookup cannot order, but a record-store index never ties.
/// </summary>
public class ZoneTimelineTests
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);
    private readonly RecordStore _records = new();

    private static DamageEvent Swing(string defender) =>
        new("Moonchopper", defender, 5, DamageKind.Melee, "Crushes");

    [Fact]
    public void RecordsBeforeTheFirstZoneLineAreUnknown()
    {
        _records.Append(T0, Swing("A rat"));
        _records.Append(T0.AddSeconds(1), new ZoneEvent("The Estate of Unrest"));

        var timeline = ZoneTimeline.Build(_records);

        Assert.Null(timeline.ZoneAt(0));
        Assert.Equal("The Estate of Unrest", timeline.ZoneAt(1)!.Value.BaseName);
    }

    [Fact]
    public void AZoneCloseWithNoNameGoesUnknownUntilTheNextEntry()
    {
        _records.Append(T0, new ZoneEvent("Zone A"));               // 0
        _records.Append(T0.AddSeconds(1), Swing("A rat"));           // 1: Zone A
        _records.Append(T0.AddSeconds(2), new ZoneEvent(null));      // 2: LOADING
        _records.Append(T0.AddSeconds(3), Swing("A bat"));           // 3: unknown
        _records.Append(T0.AddSeconds(4), new ZoneEvent("Zone B"));  // 4
        _records.Append(T0.AddSeconds(5), Swing("A cat"));           // 5: Zone B

        var timeline = ZoneTimeline.Build(_records);

        Assert.Equal("Zone A", timeline.ZoneAt(1)!.Value.BaseName);
        Assert.Null(timeline.ZoneAt(3));
        Assert.Equal("Zone B", timeline.ZoneAt(5)!.Value.BaseName);
    }

    /// <summary>
    /// The "Welcome to EverQuest!" login marker closes a zone exactly like a
    /// load screen — it carries no zone name either.
    /// </summary>
    [Fact]
    public void WelcomeAlsoClosesTheZone()
    {
        _records.Append(T0, new ZoneEvent("Zone A"));
        _records.Append(T0.AddSeconds(1), Swing("A rat"));
        _records.Append(T0.AddSeconds(2), new ZoneEvent(null, Welcome: true));
        _records.Append(T0.AddSeconds(3), Swing("A bat"));

        var timeline = ZoneTimeline.Build(_records);

        Assert.Equal("Zone A", timeline.ZoneAt(1)!.Value.BaseName);
        Assert.Null(timeline.ZoneAt(3));
    }

    /// <summary>
    /// The crux of C3. A zone line and the very next record land in the SAME
    /// second — routine on this game — so a purely time-keyed lookup could not
    /// tell them apart (Recon point 3). Every log line is its own record-store
    /// index, so index order never has this problem.
    /// </summary>
    [Fact]
    public void IndexOrderResolvesRecordsSharingATimestampWithTheZoneLine()
    {
        _records.Append(T0, new ZoneEvent("Zone A"));   // 0
        _records.Append(T0, Swing("A rat"));             // 1, same second, still Zone A
        _records.Append(T0, new ZoneEvent("Zone B"));    // 2, same second again
        _records.Append(T0, Swing("A bat"));             // 3, same second, now Zone B

        var timeline = ZoneTimeline.Build(_records);

        Assert.Equal("Zone A", timeline.ZoneAt(1)!.Value.BaseName);
        Assert.Equal("Zone B", timeline.ZoneAt(3)!.Value.BaseName);
    }

    [Fact]
    public void ALogWithNoZoneLinesReportsEverythingUnknown()
    {
        _records.Append(T0, Swing("A rat"));
        _records.Append(T0.AddSeconds(1), Swing("A bat"));

        var timeline = ZoneTimeline.Build(_records);

        Assert.True(timeline.IsEmpty);
        Assert.Null(timeline.ZoneAt(0));
        Assert.Null(timeline.ZoneAt(1));
    }
}

/// <summary>
/// C5's encoding, ruled by the architect 2026-09-20 (ADR-022 Decision 5):
/// seconds since the Unix epoch, reading the log's wall clock as if it were
/// UTC. Cross-checked against Node's <c>Date.UTC(...)/1000</c> for the same
/// wall-clock digits — see the implementer report for the `node -e` tail.
/// </summary>
public class EpochEncodingRoundTripTests
{
    [Theory]
    [InlineData(2024, 3, 9, 20, 0, 3, 1710014403d)]     // a death in the big integration test below
    [InlineData(2024, 3, 9, 20, 0, 31, 1710014431d)]    // the last death in the same test
    [InlineData(1970, 1, 1, 0, 0, 0, 0d)]               // the epoch itself — see the report's caveat
    [InlineData(1999, 3, 16, 0, 0, 0, 921542400d)]      // EverQuest's own release date: an old log
    [InlineData(2099, 12, 31, 23, 59, 59, 4102444799d)] // a comfortably-future edge
    public void WallClockRoundTripsExactlyToTheSecond(
        int year, int month, int day, int hour, int minute, int second, double expectedEpoch)
    {
        // Log timestamps are DateTimeKind.Unspecified local time with no zone
        // (LogTimestamp.TryParse) — this constructor mirrors that exactly.
        var timestamp = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);

        // The production formula (MetricCatalog.ToEpochSeconds), reproduced
        // here rather than called, per CLAUDE.md §8: the test must not assert
        // the engine against its own output.
        var epoch = (timestamp - DateTime.UnixEpoch).TotalSeconds;
        Assert.Equal(expectedEpoch, epoch);

        // The C# mirror of the UI's `new Date(value * 1000)`.
        var reconstructed = DateTime.UnixEpoch.AddSeconds(epoch);
        Assert.Equal(timestamp, reconstructed); // DateTime equality ignores Kind
    }
}

/// <summary>
/// The hand-computed acceptance test: one synthetic log driven through the
/// real <see cref="Session"/> pipeline, replayed against every named
/// acceptance case in one pass.
///
/// Timeline (T0 = 2024-03-09 20:00:00, offsets in seconds):
///   t0  Moonchopper hits A rat for 5           — BEFORE any zone line: (unknown)/(unknown)
///   t1  enters "The Estate of Unrest 1 (Awakened)"
///   t2  Moonchopper hits A froglok for 100     — Estate, "1 (Awakened)"
///   t3  XP 1.000%, then slays A froglok        — same second: CREDITED
///   t4  LOADING, PLEASE WAIT...
///   t5  Moonchopper hits A shade for 10        — BETWEEN LOADING and the next entry: (unknown)/(unknown)
///   t6  enters "The Estate of Unrest 4 (Refined)"
///   t7  Moonchopper hits A ghoul for 80        — Estate, "4 (Refined)"
///   t8  slays A ghoul, no preceding XP         — seen, NOT credited
///   t9  enters "The Plane of Fear - Group 3 (Fused)"
///   t10 Moonchopper hits A cyclops for 120, A cyclops guard for 60 — Fear, "Group 3 (Fused)"
///   t11 XP 2.000%, slays A cyclops             — same second: CREDITED
///   t11 XP 2.500%, slays A cyclops guard        — same second, its OWN line: CREDITED
///   t12 enters "The Plane of Fear 3 (Fused)"   — same tier, no mode: a second Difficulty row for Fear
///   t13 Moonchopper hits A ghost for 40, XP 0.750%
///   t16 slays A ghost                          — XP was 3s earlier: NOT credited
///   t17 XP 5.000% (a quest hand-in, no kill ever follows)
///   t30 Moonchopper hits A skeleton for 20
///   t31 slays A skeleton                       — the t17 XP is 14s stale: credits nothing later
///
/// Fight totals: 5+100+10+80+120+60+40+20 = 435.
/// By zone: (unknown)=15, Estate=180 (100+80), Fear=240 (120+60+40+20). Sum 435.
/// By difficulty: (unknown)=15, "1 (Awakened)"=100, "4 (Refined)"=80,
///   "Group 3 (Fused)"=180, "3 (Fused)"=60. Sum 435. Two difficulty rows for
///   each of Estate and Fear, one zone row each — the acceptance case.
/// Deaths: froglok(credited), ghoul(seen), cyclops(credited), cyclops
///   guard(credited), ghost(seen), skeleton(seen) — 6 seen, 3 credited.
/// </summary>
public sealed class ZoneAndDifficultyQueryTests : IDisposable
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);
    private readonly string _dir;
    private readonly string _path;

    public ZoneAndDifficultyQueryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "eqlog_Moonchopper_test.txt");
    }

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

    private static string Line(int t, string action) => SyntheticLogGenerator.Prefix(T0.AddSeconds(t)) + action;

    private static string Hit(string defender, int amount) =>
        $"Moonchopper crushes {defender} for {amount} points of damage.";

    private async Task<(Session Session, QueryEngine Engine)> BuildSessionAsync()
    {
        File.WriteAllLines(_path,
        [
            Line(0, Hit("a rat", 5)),
            Line(1, "You have entered The Estate of Unrest 1 (Awakened)."),
            Line(2, Hit("a froglok", 100)),
            Line(3, "You gain experience! (1.000%)"),
            Line(3, "You have slain a froglok!"),
            Line(4, "LOADING, PLEASE WAIT..."),
            Line(5, Hit("a shade", 10)),
            Line(6, "You have entered The Estate of Unrest 4 (Refined)."),
            Line(7, Hit("a ghoul", 80)),
            Line(8, "You have slain a ghoul!"),
            Line(9, "You have entered The Plane of Fear - Group 3 (Fused)."),
            Line(10, Hit("a cyclops", 120)),
            Line(10, Hit("a cyclops guard", 60)),
            Line(11, "You gain experience! (2.000%)"),
            Line(11, "You have slain a cyclops!"),
            Line(11, "You gain experience! (2.500%)"),
            Line(11, "You have slain a cyclops guard!"),
            Line(12, "You have entered The Plane of Fear 3 (Fused)."),
            Line(13, Hit("a ghost", 40)),
            Line(13, "You gain experience! (0.750%)"),
            Line(16, "You have slain a ghost!"),
            Line(17, "You gain experience! (5.000%)"),
            Line(30, Hit("a skeleton", 20)),
            Line(31, "You have slain a skeleton!"),
        ]);

        var session = new Session(_path, ingestOptions: new IngestOptions { Follow = false });
        await session.RunAsync(CancellationToken.None);

        Assert.Equal(0, session.UnrecognizedLines);
        Assert.Equal(0, session.Ingestion.MalformedLines);

        return (session, new QueryEngine(session));
    }

    private static QueryRow Row(IReadOnlyList<QueryRow> rows, string key) => rows.Single(r => r.Key == key);

    [Fact]
    public async Task DamageByZoneSumsToTheFightTotals()
    {
        var (session, engine) = await BuildSessionAsync();

        var fightTotal = session.Fights.Fights.Sum(f => f.DamageTotal);
        Assert.Equal(435, fightTotal);

        var byZone = engine.Execute(new QuerySpec
        {
            Source = QuerySource.Damage,
            GroupBy = [Dimension.Zone],
            Metrics = ["total"],
        });

        Assert.Equal(15, Row(byZone.Rows, ZoneTimeline.Unknown).Metrics["total"]);
        Assert.Equal(180, Row(byZone.Rows, "The Estate of Unrest").Metrics["total"]);
        Assert.Equal(240, Row(byZone.Rows, "The Plane of Fear").Metrics["total"]);
        Assert.Equal(435, byZone.Rows.Sum(r => r.Metrics["total"]));
        Assert.Equal(435, byZone.Totals["total"]);
        Assert.Equal(fightTotal, byZone.Totals["total"]);
    }

    /// <summary>
    /// The named acceptance case: one place at two tiers (Estate), one place
    /// entered "- Group" and re-entered unmarked at the same tier (Fear) — two
    /// Difficulty rows each, one Zone row each.
    /// </summary>
    [Fact]
    public async Task OnePlaceAtTwoTiersAndOnePlaceGroupVersusUnmarkedYieldTwoDifficultyRowsEach()
    {
        var (_, engine) = await BuildSessionAsync();

        var byDifficulty = engine.Execute(new QuerySpec
        {
            Source = QuerySource.Damage,
            GroupBy = [Dimension.Difficulty],
            Metrics = ["total"],
        });

        Assert.Equal(15, Row(byDifficulty.Rows, ZoneTimeline.Unknown).Metrics["total"]);
        Assert.Equal(100, Row(byDifficulty.Rows, "1 (Awakened)").Metrics["total"]);
        Assert.Equal(80, Row(byDifficulty.Rows, "4 (Refined)").Metrics["total"]);
        Assert.Equal(180, Row(byDifficulty.Rows, "Group 3 (Fused)").Metrics["total"]);
        Assert.Equal(60, Row(byDifficulty.Rows, "3 (Fused)").Metrics["total"]);
        Assert.Equal(5, byDifficulty.Rows.Count); // unknown + 4 distinct difficulty labels
    }

    [Fact]
    public async Task RecordsOutsideAnyKnownZoneKeyToUnknownOnBothDimensions()
    {
        var (_, engine) = await BuildSessionAsync();

        // A rat (t0, before the first zone line) and A shade (t5, between
        // LOADING and the next entry) are the only two records with no known
        // zone; isolating them by target proves BOTH collapse to one
        // "(unknown)" row rather than leaking into a neighbouring zone.
        var spec = new QuerySpec
        {
            Source = QuerySource.Damage,
            Filters = [new QueryFilter { Dim = Dimension.Target, Values = ["A rat", "A shade"] }],
        };

        var byZone = engine.Execute(spec with { GroupBy = [Dimension.Zone] });
        var zoneRow = Assert.Single(byZone.Rows);
        Assert.Equal(ZoneTimeline.Unknown, zoneRow.Key);
        Assert.Equal(15, zoneRow.Metrics["total"]);

        var byDifficulty = engine.Execute(spec with { GroupBy = [Dimension.Difficulty] });
        var difficultyRow = Assert.Single(byDifficulty.Rows);
        Assert.Equal(ZoneTimeline.Unknown, difficultyRow.Key);
        Assert.Equal(15, difficultyRow.Metrics["total"]);
    }

    [Fact]
    public async Task DeathsCarryDeathsCreditedFirstAtAndLastAtPerPlayerZoneAndDifficulty()
    {
        var (_, engine) = await BuildSessionAsync();

        var result = engine.Execute(new QuerySpec
        {
            Source = QuerySource.Deaths,
            GroupBy = [Dimension.Player, Dimension.Zone, Dimension.Difficulty],
            Metrics = ["deaths", "credited", "firstAt", "lastAt"],
            PetRollup = false, // no pet indirection needed; keeps the tree shape simple to drill
        });

        Assert.Equal(6, result.Rows.Count);

        AssertDeath(result, "A froglok", "The Estate of Unrest", "1 (Awakened)", credited: true, atSeconds: 3);
        AssertDeath(result, "A ghoul", "The Estate of Unrest", "4 (Refined)", credited: false, atSeconds: 8);
        AssertDeath(result, "A cyclops", "The Plane of Fear", "Group 3 (Fused)", credited: true, atSeconds: 11);
        AssertDeath(result, "A cyclops guard", "The Plane of Fear", "Group 3 (Fused)", credited: true, atSeconds: 11);
        AssertDeath(result, "A ghost", "The Plane of Fear", "3 (Fused)", credited: false, atSeconds: 16);
        // The orphaned t17 experience line (a quest hand-in with no kill of
        // its own) must not reach forward and credit this one, 14 s later.
        AssertDeath(result, "A skeleton", "The Plane of Fear", "3 (Fused)", credited: false, atSeconds: 31);
    }

    private static void AssertDeath(
        QueryResult result, string victim, string zone, string difficulty, bool credited, int atSeconds)
    {
        var playerRow = Row(result.Rows, victim);
        var zoneRow = Row(playerRow.Children!, zone);
        var difficultyRow = Row(zoneRow.Children!, difficulty);

        Assert.Equal(1, difficultyRow.Metrics["deaths"]);
        Assert.Equal(credited ? 1 : 0, difficultyRow.Metrics["credited"]);

        // Independently computed, per CLAUDE.md §8 — not by calling
        // MetricCatalog itself.
        var expectedEpoch = (T0.AddSeconds(atSeconds) - DateTime.UnixEpoch).TotalSeconds;
        Assert.Equal(expectedEpoch, difficultyRow.Metrics["firstAt"]);
        Assert.Equal(expectedEpoch, difficultyRow.Metrics["lastAt"]);
    }

    [Fact]
    public async Task AValuesFilterOnZoneNarrowsDeathsToThatZone()
    {
        var (_, engine) = await BuildSessionAsync();

        var result = engine.Execute(new QuerySpec
        {
            Source = QuerySource.Deaths,
            GroupBy = [Dimension.Player],
            Metrics = ["deaths"],
            Filters = [new QueryFilter { Dim = Dimension.Zone, Values = ["The Plane of Fear"] }],
        });

        Assert.Equal(4, result.Rows.Count); // cyclops, cyclops guard, ghost, skeleton
        Assert.Equal(4, result.Rows.Sum(r => r.Metrics["deaths"]));
        Assert.DoesNotContain(result.Rows, r => r.Key is "A froglok" or "A ghoul");
    }

    [Fact]
    public async Task AFilterOnDifficultyNarrowsToThatTierAndMode()
    {
        var (_, engine) = await BuildSessionAsync();

        var result = engine.Execute(new QuerySpec
        {
            Source = QuerySource.Deaths,
            GroupBy = [Dimension.Player],
            Metrics = ["deaths", "credited"],
            Filters = [new QueryFilter { Dim = Dimension.Difficulty, Values = ["Group 3 (Fused)"] }],
        });

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(2, result.Rows.Sum(r => r.Metrics["deaths"]));
        Assert.Equal(2, result.Rows.Sum(r => r.Metrics["credited"])); // both credited
    }

    // ---- C4: free when unused ----------------------------------------------

    [Fact]
    public async Task ZoneAndCreditStructuresAreNeverBuiltForAQueryThatDoesNotMentionThem()
    {
        var (_, engine) = await BuildSessionAsync();

        engine.Execute(new QuerySpec
        {
            Source = QuerySource.Damage,
            GroupBy = [Dimension.Player],
            Metrics = ["total"],
        });
        engine.Execute(new QuerySpec
        {
            Source = QuerySource.Deaths,
            GroupBy = [Dimension.Player],
            Metrics = ["deaths"], // deaths, but not credited
        });

        Assert.Equal(0, engine.ZoneBuildCount);
        Assert.Equal(0, engine.CreditedBuildCount);
    }

    /// <summary>
    /// Built on first use, then reused — not rebuilt — across further queries
    /// at the same record-store version, whether they ask by grouping or by
    /// filtering, and whether they are the same spec or a different one
    /// (ruling out "the whole-query cache is doing this, not the structure's
    /// own version cache").
    /// </summary>
    [Fact]
    public async Task ZoneAndCreditStructuresAreBuiltOnceAndReusedAcrossDifferentQueries()
    {
        var (_, engine) = await BuildSessionAsync();

        engine.Execute(new QuerySpec { Source = QuerySource.Damage, GroupBy = [Dimension.Zone] });
        Assert.Equal(1, engine.ZoneBuildCount);
        Assert.Equal(0, engine.CreditedBuildCount);

        engine.Execute(new QuerySpec
        {
            Source = QuerySource.Damage,
            GroupBy = [Dimension.Player],
            Filters = [new QueryFilter { Dim = Dimension.Difficulty, Values = ["1 (Awakened)"] }],
        });
        Assert.Equal(1, engine.ZoneBuildCount); // still one build, different spec

        engine.Execute(new QuerySpec
        {
            Source = QuerySource.Deaths,
            GroupBy = [Dimension.Player],
            Metrics = ["credited"],
        });
        Assert.Equal(1, engine.CreditedBuildCount);

        engine.Execute(new QuerySpec
        {
            Source = QuerySource.Deaths,
            GroupBy = [Dimension.Zone],
            Metrics = ["deaths", "credited"],
        });
        Assert.Equal(1, engine.CreditedBuildCount); // still one build, different spec
        Assert.Equal(1, engine.ZoneBuildCount); // and zones were not rebuilt either
    }

    // ---- scope units arriving out of ascending record-index order ---------

    /// <summary>
    /// The architect's pinned concern: <c>ResolveScope</c> does not guarantee
    /// scope units arrive in ascending record-index order (an explicit
    /// <see cref="QueryScope.TimeRanges"/> lists them exactly as the caller
    /// gave them). Zone resolution is a fresh binary search per record rather
    /// than a cursor carried across units, so it must not care. This spec
    /// deliberately lists the LATER range (the Fear ghost kill, t12-17) before
    /// the EARLIER one (the Estate froglok kill, t1-4) — Damage is a
    /// per-fight source, so the two ranges are NOT collapsed to a union
    /// first, and the resulting units really do walk indices out of order.
    /// </summary>
    [Fact]
    public async Task ZoneResolutionIsCorrectWhenScopeUnitsArriveOutOfAscendingIndexOrder()
    {
        var (_, engine) = await BuildSessionAsync();

        var laterRange = new TimeRange(T0.AddSeconds(12), T0.AddSeconds(17)); // A ghost, Fear "3 (Fused)"
        var earlierRange = new TimeRange(T0.AddSeconds(1), T0.AddSeconds(4)); // A froglok, Estate "1 (Awakened)"

        var result = engine.Execute(new QuerySpec
        {
            Source = QuerySource.Damage,
            Scope = new QueryScope { TimeRanges = [laterRange, earlierRange] },
            GroupBy = [Dimension.Zone],
            Metrics = ["total"],
        });

        Assert.Equal(40, Row(result.Rows, "The Plane of Fear").Metrics["total"]);
        Assert.Equal(100, Row(result.Rows, "The Estate of Unrest").Metrics["total"]);
        Assert.Equal(2, result.Rows.Count);
    }
}
