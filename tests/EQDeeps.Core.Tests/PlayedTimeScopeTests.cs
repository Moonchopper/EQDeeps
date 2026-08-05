using EQDeeps.Core.Events;
using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// A range is a stretch of wall clock, and wall clock includes the night. Rates
/// read over one therefore answer two different questions depending on what the
/// denominator is made of — plat per hour PLAYED, or plat per hour that passed —
/// and <see cref="QueryScope.PlayedTimeOnly"/> is which one is being asked.
///
/// The mechanism is the scope, not the metric: cutting the range into one unit
/// per play session is what stops a single unit stretching across the gap, and
/// every duration and rate follows from that.
/// </summary>
public class PlayedTimeScopeTests
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);

    private readonly IdentityRegistry _identity = new();
    private readonly RecordStore _records = new();
    private readonly FightTracker _tracker;
    private readonly QueryEngine _engine;

    public PlayedTimeScopeTests()
    {
        _identity.AddVerifiedPlayer("Moonchopper");
        _tracker = new FightTracker(_identity);
        _engine = new QueryEngine(_records, _tracker, _identity, "Moonchopper");

        // Two evenings of an hour each, nine hours apart.
        Evening(0);
        Evening(10 * 3600);
    }

    /// <summary>
    /// An hour of pulls, with a platinum looted every five minutes. Records go
    /// in in timestamp order, as ingestion delivers them: presence is read from
    /// the gaps between consecutive records, so a fixture that appends out of
    /// order measures nothing but its own mistake.
    /// </summary>
    private void Evening(double offset)
    {
        for (var t = 0; t < 3600; t += 60)
        {
            Add(offset + t, new DamageEvent("Moonchopper", "A froglok", 10, DamageKind.Melee, "Crushes"));
            if (t % 300 == 0)
            {
                Add(offset + t, new LootEvent("Moonchopper", null, "corpse", Copper: 1000));
            }
        }
    }

    private void Add(double seconds, GameEvent evt)
    {
        var timestamp = T0.AddSeconds(seconds);
        _records.Append(timestamp, evt);
        _tracker.Process(timestamp, evt);
    }

    private QueryResult Loot(bool playedTimeOnly) =>
        _engine.Execute(new QuerySpec
        {
            Source = QuerySource.Loot,
            Scope = new QueryScope
            {
                // The whole file, framed as a range — what typing a window or
                // promoting a zoom produces.
                TimeRanges = [new TimeRange(T0, T0.AddSeconds(11 * 3600))],
                PlayedTimeOnly = playedTimeOnly,
            },
            GroupBy = [Dimension.Player],
            Metrics = ["platinum", "platPerHour", "raidSeconds"],
        });

    /// <summary>The default: the range means what it says, night included.</summary>
    [Fact]
    public void AWallClockRangeCountsTheNight()
    {
        var totals = Loot(playedTimeOnly: false).Totals;

        Assert.Equal(24, totals["platinum"], 3);
        Assert.InRange(totals["raidSeconds"], 10.5 * 3600, 11 * 3600);
        Assert.InRange(totals["platPerHour"], 2.0, 2.4);
    }

    /// <summary>The same 24 plat, over the two hours anyone was there for.</summary>
    [Fact]
    public void PlayedTimeOnlyCutsTheNightOut()
    {
        var totals = Loot(playedTimeOnly: true).Totals;

        Assert.Equal(24, totals["platinum"], 3);
        // Two evenings' worth rather than eleven hours' — a shade under two,
        // since the measure runs first-loot to last-loot inside each one.
        Assert.InRange(totals["raidSeconds"], 1.75 * 3600, 2 * 3600);
        // The loot did not change; the hours it is divided by did.
        Assert.InRange(totals["platPerHour"], 12.0, 14.0);
    }

    /// <summary>
    /// The flag is about the gaps BETWEEN sessions, not the quiet inside one —
    /// a camp break is time the player was still sitting there for.
    /// </summary>
    [Fact]
    public void ALullInsideASessionChangesNothing()
    {
        var records = new RecordStore();
        var identity = new IdentityRegistry();
        var engine = new QueryEngine(records, new FightTracker(identity), identity, "Moonchopper");
        for (var t = 0; t <= 300; t += 60)
        {
            records.Append(T0.AddSeconds(t), new LootEvent("Moonchopper", null, "corpse", Copper: 1000));
        }

        QueryResult Run(bool played) =>
            engine.Execute(new QuerySpec
            {
                Source = QuerySource.Loot,
                Scope = new QueryScope
                {
                    TimeRanges = [new TimeRange(T0, T0.AddSeconds(300))],
                    PlayedTimeOnly = played,
                },
                GroupBy = [Dimension.Player],
                Metrics = ["platinum", "platPerHour", "raidSeconds"],
            });

        Assert.Equal(Run(false).Totals["raidSeconds"], Run(true).Totals["raidSeconds"], 3);
        Assert.Equal(Run(false).Totals["platPerHour"], Run(true).Totals["platPerHour"], 3);
    }

    /// <summary>
    /// Fights carry their own begin and end, so a scope made of them never
    /// contained the night and the flag has nothing to take away. Worth
    /// pinning: the intersection runs over every unit, and fights coming back
    /// clipped would quietly move every DPS on the screen.
    /// </summary>
    [Fact]
    public void AFightScopeIsUnaffected()
    {
        QueryResult Damage(bool played) =>
            _engine.Execute(new QuerySpec
            {
                Source = QuerySource.Damage,
                Scope = new QueryScope { PlayedTimeOnly = played },
                GroupBy = [Dimension.Player],
                Metrics = ["total", "dps", "sdps"],
            });

        var wall = Damage(false).Totals;
        var play = Damage(true).Totals;
        Assert.Equal(1200, wall["total"], 3);
        Assert.Equal(wall["total"], play["total"], 3);
        Assert.Equal(wall["dps"], play["dps"], 3);
        Assert.Equal(wall["sdps"], play["sdps"], 3);
    }
}
