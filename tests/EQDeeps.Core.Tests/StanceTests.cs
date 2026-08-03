using EQDeeps.Core.Events;
using EQDeeps.Core.Parsing;
using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// Stance tracking end to end: the switch grammar, the exclusive spans it
/// implies, and the per-stance aggregation built on them.
/// </summary>
public class StanceParserTests
{
    private static readonly ParserOptions Options = new("Moonchopper");

    private static GameEvent? Parse(string line) => new LogEventParser(Options).Parse(line);

    [Theory]
    [InlineData("You assume a defensive stance.", "Defensive")]
    [InlineData("You assume a berserker stance.", "Berserker")]
    [InlineData("You assume an offensive stance.", "Offensive")]
    [InlineData("You assume an evasive fighting style.", "Evasive")]
    [InlineData("You assume a precision stance.", "Precision")]
    public void ParsesOwnerStanceSwitches(string line, string expected)
    {
        var stance = Assert.IsType<StanceEvent>(Parse(line));
        Assert.Equal("Moonchopper", stance.Player);
        Assert.Equal(expected, stance.Stance);
    }

    /// <summary>
    /// The point of matching on shape rather than a known list: a stance the
    /// server adds tomorrow shows up as itself instead of disappearing.
    /// </summary>
    [Fact]
    public void ParsesStanceNamesItHasNeverSeen()
    {
        var stance = Assert.IsType<StanceEvent>(Parse("You assume a bloodthirsty stance."));
        Assert.Equal("Bloodthirsty", stance.Stance);
    }

    [Theory]
    [InlineData("You return to your normal stance.")]
    [InlineData("You resume your normal stance.")]
    public void DroppingAStanceIsItsOwnState(string line)
    {
        var stance = Assert.IsType<StanceEvent>(Parse(line));
        Assert.Equal("Normal", stance.Stance);
    }

    /// <summary>The switch lands on the "assume" line a beat later, not on this one.</summary>
    [Fact]
    public void BeginningAChangeIsNotAStateChange()
    {
        Assert.Null(Parse("You begin to change your stance."));
    }

    [Fact]
    public void ParsesAnotherActorsSwitch()
    {
        var stance = Assert.IsType<StanceEvent>(Parse("Grimwald assumes a defensive stance."));
        Assert.Equal("Grimwald", stance.Player);
        Assert.Equal("Defensive", stance.Stance);
    }

    /// <summary>Flavour text that merely ends in "stance" is not a switch.</summary>
    [Theory]
    [InlineData("The temple guards fan out and settle into a well practiced defensive shield stance.")]
    [InlineData("You have entered an Instanced Version of the zone.")]
    public void IgnoresSentencesThatMerelyMentionStance(string line)
    {
        Assert.IsNotType<StanceEvent>(Parse(line));
    }

    /// <summary>Chat runs first, so a player quoting the line cannot forge a switch.</summary>
    [Fact]
    public void ChatCannotForgeAStanceSwitch()
    {
        var evt = Parse("Grimwald says out of character, 'You assume a defensive stance.'");
        Assert.IsType<ChatEvent>(evt);
    }
}

public class StanceTimelineTests
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);

    private readonly RecordStore _records = new();

    private void Add(int t, GameEvent evt) => _records.Append(T0.AddSeconds(t), evt);

    [Fact]
    public void SwitchesTileTheSessionWithoutGaps()
    {
        Add(0, new DamageEvent("Moonchopper", "A froglok", 10, DamageKind.Melee, "Crushes"));
        Add(10, new StanceEvent("Moonchopper", "Defensive"));
        Add(20, new StanceEvent("Moonchopper", "Berserker"));
        Add(30, new DamageEvent("Moonchopper", "A froglok", 10, DamageKind.Melee, "Crushes"));

        var timeline = StanceTimeline.Build(_records, "Moonchopper");

        Assert.Collection(
            timeline.Spans,
            s =>
            {
                // Before the first switch we know something was held, not what.
                Assert.Equal(StanceTimeline.Unknown, s.Stance);
                Assert.Equal(T0, s.Begin);
                Assert.Equal(T0.AddSeconds(9), s.End);
            },
            s =>
            {
                Assert.Equal("Defensive", s.Stance);
                Assert.Equal(T0.AddSeconds(10), s.Begin);
                Assert.Equal(T0.AddSeconds(19), s.End);
            },
            s =>
            {
                // The last stance runs to the end of the log, not to its switch.
                Assert.Equal("Berserker", s.Stance);
                Assert.Equal(T0.AddSeconds(20), s.Begin);
                Assert.Equal(T0.AddSeconds(30), s.End);
            });
    }

    [Fact]
    public void IgnoresOtherActorsSwitches()
    {
        Add(0, new StanceEvent("Grimwald", "Defensive"));
        Add(10, new DamageEvent("Moonchopper", "A froglok", 10, DamageKind.Melee, "Crushes"));

        Assert.True(StanceTimeline.Build(_records, "Moonchopper").IsEmpty);
    }

    [Fact]
    public void ResolvesTheStanceHeldAtAnInstant()
    {
        Add(0, new StanceEvent("Moonchopper", "Defensive"));
        Add(10, new StanceEvent("Moonchopper", "Berserker"));
        Add(20, new DamageEvent("Moonchopper", "A froglok", 10, DamageKind.Melee, "Crushes"));

        var timeline = StanceTimeline.Build(_records, "Moonchopper");

        Assert.Equal("Defensive", timeline.StanceAt(T0.AddSeconds(9)));
        Assert.Equal("Berserker", timeline.StanceAt(T0.AddSeconds(10)));
        Assert.Equal("Berserker", timeline.StanceAt(T0.AddSeconds(20)));
    }
}

/// <summary>
/// Per-stance aggregation, hand-computed.
///
/// Moonchopper switches to Defensive at t0 and to Berserker at t20, so the raw
/// stance spans are [t0..t19] and [t20..t39]. The scope is the FIGHT, t1..t39
/// (first damage to the kill), and the stance spans are clipped to it:
///   Defensive [t1..t19]  = 19 s held; 100 @t1 + 100 @t18  →  200 / 19 s
///   Berserker [t20..t39] = 20 s held; 400 @t21 + 600 @t38 → 1000 / 20 s
///   Raider02 (not the owner): 500 @t22                    → "(not you)"
/// Raid seconds are the damage records' own span, t1..t38 = 38 s, which is why
/// uptime is read against the 39 s of tracked stance rather than against it.
/// </summary>
public class StanceQueryTests
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);

    private readonly IdentityRegistry _identity = new();
    private readonly RecordStore _records = new();
    private readonly FightTracker _tracker;
    private readonly QueryEngine _engine;

    public StanceQueryTests()
    {
        _identity.AddVerifiedPlayer("Moonchopper");
        _identity.AddVerifiedPlayer("Raider02");
        _tracker = new FightTracker(_identity);
        _engine = new QueryEngine(_records, _tracker, _identity, "Moonchopper");

        Add(0, new StanceEvent("Moonchopper", "Defensive"));
        Add(1, Swing("Moonchopper", 100));
        Add(18, Swing("Moonchopper", 100));
        Add(20, new StanceEvent("Moonchopper", "Berserker"));
        Add(21, Swing("Moonchopper", 400));
        Add(22, Swing("Raider02", 500));
        Add(38, Swing("Moonchopper", 600));
        Add(39, new DeathEvent("A froglok ton knight", "Moonchopper"));
    }

    private static DamageEvent Swing(string attacker, uint amount) =>
        new(attacker, "A froglok ton knight", amount, DamageKind.Melee, "Crushes");

    private void Add(int t, GameEvent evt)
    {
        var timestamp = T0.AddSeconds(t);
        _records.Append(timestamp, evt);
        _tracker.Process(timestamp, evt);
    }

    private QueryResult ByStance() => _engine.Execute(new QuerySpec
    {
        Source = QuerySource.Damage,
        GroupBy = [Dimension.Stance],
        Metrics = ["total", "stanceSeconds", "stanceDps", "stanceUptime", "dps"],
    });

    private static QueryRow Row(QueryResult result, string key) =>
        result.Rows.Single(r => r.Key == key);

    [Fact]
    public void SplitsDamageByTheStanceHeldAtTheTime()
    {
        var result = ByStance();

        Assert.Equal(200, Row(result, "Defensive").Metrics["total"]);
        Assert.Equal(1000, Row(result, "Berserker").Metrics["total"]);
    }

    /// <summary>
    /// The point of the whole feature: stance DPS divides by the time the
    /// stance was HELD, so the idle seconds inside it are not refunded the way
    /// plain active time refunds them.
    /// </summary>
    [Fact]
    public void StanceDpsDividesByTimeHeldNotTimeSwinging()
    {
        var result = ByStance();

        var defensive = Row(result, "Defensive");
        Assert.Equal(19, defensive.Metrics["stanceSeconds"]);
        Assert.Equal(200 / 19.0, defensive.Metrics["stanceDps"], 3);
        // Active time is only t1..t18 — 18 s — which would flatter the stance
        // by refunding the second it was held without swinging.
        Assert.Equal(200 / 18.0, defensive.Metrics["dps"], 3);

        var berserker = Row(result, "Berserker");
        Assert.Equal(20, berserker.Metrics["stanceSeconds"]);
        Assert.Equal(50.0, berserker.Metrics["stanceDps"], 3);
    }

    /// <summary>Uptimes are shares of one whole, so they have to sum to 100%.</summary>
    [Fact]
    public void UptimeIsAShareOfTheTrackedStanceTime()
    {
        var result = ByStance();

        var defensive = Row(result, "Defensive").Metrics["stanceUptime"];
        var berserker = Row(result, "Berserker").Metrics["stanceUptime"];
        Assert.Equal(1900 / 39.0, defensive, 3);
        Assert.Equal(2000 / 39.0, berserker, 3);
        Assert.Equal(100.0, defensive + berserker, 6);
    }

    /// <summary>
    /// Another player's stance was written to THEIR log, so their damage is
    /// labelled rather than folded into whatever the owner was holding.
    /// </summary>
    [Fact]
    public void OtherPlayersAreNotAttributedToTheOwnersStance()
    {
        var result = ByStance();

        var other = Row(result, StanceTimeline.NotTracked);
        Assert.Equal(500, other.Metrics["total"]);
        Assert.Equal(0, other.Metrics["stanceSeconds"]);
        Assert.Equal(1000, Row(result, "Berserker").Metrics["total"]); // not 1500
    }

    [Fact]
    public void StanceFiltersSelectOneStance()
    {
        var result = _engine.Execute(new QuerySpec
        {
            Source = QuerySource.Damage,
            GroupBy = [Dimension.Player],
            Metrics = ["total"],
            Filters = [new QueryFilter { Dim = Dimension.Stance, Values = ["Berserker"] }],
        });

        Assert.Equal(1000, Row(result, "Moonchopper").Metrics["total"]);
    }

    /// <summary>A log with no stance lines must not grow a phantom dimension.</summary>
    [Fact]
    public void LogsWithoutStancesReportEverythingAsUnknown()
    {
        var records = new RecordStore();
        var identity = new IdentityRegistry();
        identity.AddVerifiedPlayer("Moonchopper");
        var tracker = new FightTracker(identity);
        var engine = new QueryEngine(records, tracker, identity, "Moonchopper");
        foreach (var t in new[] { 0, 1, 2 })
        {
            var timestamp = T0.AddSeconds(t);
            records.Append(timestamp, Swing("Moonchopper", 100));
            tracker.Process(timestamp, Swing("Moonchopper", 100));
        }

        var result = engine.Execute(new QuerySpec
        {
            Source = QuerySource.Damage,
            GroupBy = [Dimension.Stance],
            Metrics = ["total", "stanceSeconds"],
        });

        var row = Assert.Single(result.Rows);
        Assert.Equal(StanceTimeline.Unknown, row.Key);
        Assert.Equal(300, row.Metrics["total"]);
        Assert.Equal(0, row.Metrics["stanceSeconds"]);
    }
}
