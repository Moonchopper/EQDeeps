using EQDeeps.Core.Events;
using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// Play sessions carved out of a log that runs for months. See
/// <see cref="PresenceTimeline"/> for why a duration read straight off the
/// record stream is otherwise measuring the nights as well as the evenings.
/// </summary>
public class PresenceTimelineTests
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(10);

    private readonly RecordStore _records = new();

    private void Add(double seconds, GameEvent evt) =>
        _records.Append(T0.AddSeconds(seconds), evt);

    private static DamageEvent Swing() =>
        new("Moonchopper", "A froglok", 10, DamageKind.Melee, "Crushes");

    [Fact]
    public void AContinuousLogIsOneSession()
    {
        Add(0, Swing());
        Add(30, Swing());
        Add(60, Swing());

        var span = Assert.Single(PresenceTimeline.Build(_records, Gap).Spans);
        Assert.Equal(T0, span.Begin);
        Assert.Equal(T0.AddSeconds(60), span.End);
    }

    /// <summary>The overnight case: the log's own silence is the logout.</summary>
    [Fact]
    public void ALongQuietStretchEndsTheSession()
    {
        Add(0, Swing());
        Add(60, Swing());
        Add(9 * 3600, Swing()); // next evening
        Add(9 * 3600 + 60, Swing());

        var spans = PresenceTimeline.Build(_records, Gap).Spans;
        Assert.Equal(2, spans.Count);
        Assert.Equal(T0.AddSeconds(60), spans[0].End);
        Assert.Equal(T0.AddSeconds(9 * 3600), spans[1].Begin);
        // Nine hours of absence, counted as none of it.
        Assert.Equal(122, spans[0].TotalSeconds + spans[1].TotalSeconds);
    }

    /// <summary>A lull inside a session is not an absence.</summary>
    [Fact]
    public void ShortQuietDoesNotSplitASession()
    {
        Add(0, Swing());
        Add(120, Swing()); // two minutes of nothing — still playing
        Add(240, Swing());

        Assert.Single(PresenceTimeline.Build(_records, Gap).Spans);
    }

    /// <summary>
    /// Swapping characters can take less than the quiet threshold, so the login
    /// marker splits regardless of how brief the gap was.
    /// </summary>
    [Fact]
    public void ALoginAlwaysStartsASession()
    {
        Add(0, Swing());
        Add(60, Swing());
        Add(90, new ZoneEvent(null, Welcome: true));
        Add(120, Swing());

        var spans = PresenceTimeline.Build(_records, Gap).Spans;
        Assert.Equal(2, spans.Count);
        Assert.Equal(T0.AddSeconds(60), spans[0].End);
        Assert.Equal(T0.AddSeconds(90), spans[1].Begin);
    }

    /// <summary>A zone change is not a login and must not split anything.</summary>
    [Fact]
    public void ZoningIsNotALogin()
    {
        Add(0, Swing());
        Add(30, new ZoneEvent(null));
        Add(45, new ZoneEvent("The Feerrott"));
        Add(60, Swing());

        Assert.Single(PresenceTimeline.Build(_records, Gap).Spans);
    }

    [Fact]
    public void IntersectKeepsOnlyThePlayedParts()
    {
        Add(0, Swing());
        Add(60, Swing());
        Add(9 * 3600, Swing());
        Add(9 * 3600 + 60, Swing());

        var presence = PresenceTimeline.Build(_records, Gap);
        var whole = new TimeRange(T0, T0.AddSeconds(9 * 3600 + 60));

        Assert.Equal(122, presence.SecondsWithin(whole));
        Assert.Equal(2, presence.Intersect(whole).Count);
        // A range entirely inside the gap played out to nothing.
        Assert.Empty(presence.Intersect(new TimeRange(T0.AddSeconds(3600), T0.AddSeconds(7200))));
    }

    [Fact]
    public void AnEmptyLogHasNoSessions()
    {
        Assert.True(PresenceTimeline.Build(_records, Gap).IsEmpty);
    }
}

/// <summary>
/// What presence changes downstream: a stance held over a logout, and a rate
/// metric read over a log with nights in it.
/// </summary>
public class PresenceAwareMetricsTests
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);

    private readonly IdentityRegistry _identity = new();
    private readonly RecordStore _records = new();
    private readonly FightTracker _tracker;
    private readonly QueryEngine _engine;

    public PresenceAwareMetricsTests()
    {
        _identity.AddVerifiedPlayer("Moonchopper");
        _tracker = new FightTracker(_identity);
        _engine = new QueryEngine(_records, _tracker, _identity, "Moonchopper");
    }

    private void Add(double seconds, GameEvent evt)
    {
        var timestamp = T0.AddSeconds(seconds);
        _records.Append(timestamp, evt);
        _tracker.Process(timestamp, evt);
    }

    /// <summary>
    /// Two evenings a day apart, one stance assumed on the first and never
    /// switched. Time held is the two evenings, not the day between them.
    /// </summary>
    [Fact]
    public void AStanceHeldOverALogoutDoesNotAccrueOvernight()
    {
        Add(0, new StanceEvent("Moonchopper", "Berserker"));
        Add(1, new DamageEvent("Moonchopper", "A froglok", 100, DamageKind.Melee, "Crushes"));
        Add(30, new DamageEvent("Moonchopper", "A froglok", 100, DamageKind.Melee, "Crushes"));
        // …logged off for a day…
        Add(86400, new DamageEvent("Moonchopper", "A froglok", 100, DamageKind.Melee, "Crushes"));
        Add(86430, new DamageEvent("Moonchopper", "A froglok", 100, DamageKind.Melee, "Crushes"));

        var result = _engine.Execute(new QuerySpec
        {
            Source = QuerySource.Damage,
            GroupBy = [Dimension.Stance],
            Metrics = ["total", "stanceSeconds", "stanceDps"],
        });

        var row = Assert.Single(result.Rows);
        Assert.Equal("Berserker", row.Key);
        Assert.Equal(400, row.Metrics["total"]);
        // The two fights, t1..t30 and t86400..t86430 — 30 s and 31 s, ranges
        // being inclusive — rather than the 86,431 s the raw span between the
        // first and last record would give.
        Assert.Equal(61, row.Metrics["stanceSeconds"]);
        Assert.True(row.Metrics["stanceDps"] > 6, $"dps was {row.Metrics["stanceDps"]}");
    }

    /// <summary>
    /// Loot over a whole-log scope: the denominator is time played, so the
    /// night between two sessions cannot dilute plat per hour.
    /// </summary>
    [Fact]
    public void WholeLogRatesDivideByTimePlayedNotByTheCalendar()
    {
        // One hour of play, a day away, another hour of play. The filler is the
        // point: a session is a stretch the log keeps talking through, so four
        // lone records an hour apart really are four sessions, not two.
        foreach (var start in new[] { 0, 86400 })
        {
            for (var t = start; t <= start + 3600; t += 300)
            {
                Add(t, new DamageEvent("Moonchopper", "A froglok", 1, DamageKind.Melee, "Crushes"));
                if (t == start || t == start + 3600)
                {
                    Add(t, new LootEvent("Moonchopper", Item: null, "corpse", Copper: 1000_000));
                }
            }
        }

        var result = _engine.Execute(new QuerySpec
        {
            Source = QuerySource.Loot,
            GroupBy = [Dimension.Player],
            Metrics = ["platinum", "platPerHour", "raidSeconds"],
        });

        // Two sessions of 3,601 s each — not the 90,001 s the file spans.
        Assert.Equal(7202, result.RaidSeconds);
        Assert.Equal(4000, result.Totals["platinum"]);
        // 4,000 plat over almost exactly two hours. Across the whole file it
        // would have read about 160.
        Assert.InRange(result.Totals["platPerHour"], 1999, 2000);
    }
}
