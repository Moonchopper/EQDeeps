using EQDeeps.Core.Events;
using EQDeeps.Core.Ingestion;
using EQDeeps.Core.Sessions;
using EQDeeps.TestSupport;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// End-to-end replay: a hand-authored raid log through ingestion → parser →
/// record store / identity / fights, verified against hand-computed values
/// (HANDOFF phase-3 exit criterion).
/// </summary>
public sealed class SessionTests : IDisposable
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);
    private readonly string _dir;

    public SessionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
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

    [Fact]
    public async Task RaidReplayYieldsExpectedFightsAndCounters()
    {
        var path = Path.Combine(_dir, "eqlog_Kizant_xegony.txt");
        File.WriteAllLines(path,
        [
            Line(0, "Raider01 tells the raid, 'inc giant'"),
            Line(1, "Raider02 tells the group, 'ready'"),
            // Fight 1: An ice giant.
            Line(2, "Raider01 crushes an ice giant for 100 points of damage."),
            Line(3, "Raider01 crushes an ice giant for 200 points of damage. (Critical)"),
            Line(4, "An ice giant hits Raider01 for 150 points of damage."),
            Line(5, "Raider02 kicks an ice giant for 50 points of damage."),
            Line(6, "Raider02 hit an ice giant for 50 points of fire damage by Burst of Flames."),
            Line(7, "An ice giant tries to hit Raider02, but Raider02 dodges!"),
            Line(8, "Raider01 crushes an ice giant for 300 points of damage."),
            Line(8, "An ice giant has been slain by Raider01!"),
            // Chat that mimics combat must not corrupt anything.
            Line(9, "Raider03 tells the raid, 'Raider01 crushes an ice giant for 999999 points of damage.'"),
            // Heals never create or extend fights.
            Line(10, "Raider02 healed Raider01 for 500 hit points by Blessing of the Ancients III."),
            // Fight 2 after a 200 s break: pet + owner via pet-leader line.
            Line(208, "Xobatik says 'My leader is Raider02'"),
            Line(209, "Raider01 crushes a shadow drake for 400 points of damage."),
            Line(210, "Xobatik bites a shadow drake for 60 points of damage."),
            Line(211, "You crush a shadow drake for 40 points of damage."),
        ]);

        var session = new Session(path, ingestOptions: new IngestOptions { Follow = false });
        Assert.Equal("Kizant", session.Character);
        Assert.Equal("xegony", session.Server);

        await session.RunAsync(CancellationToken.None);

        Assert.True(session.BackfillComplete);
        Assert.Equal(0, session.UnrecognizedLines);
        Assert.Equal(0, session.Ingestion.MalformedLines);
        Assert.Equal(16, session.Records.Count);

        // Fights: hand-computed.
        Assert.Equal(2, session.Fights.Fights.Count);

        var giant = session.Fights.Fights[0];
        Assert.Equal("An ice giant", giant.Name);
        Assert.Equal(T0.AddSeconds(2), giant.BeginTime);
        Assert.Equal(T0.AddSeconds(8), giant.LastDamageTime);
        Assert.True(giant.Dead);
        Assert.True(giant.Closed);
        Assert.Equal(700, giant.DamageTotal);   // 100+200+50+50+300
        Assert.Equal(150, giant.TankingTotal);
        Assert.Equal(600, giant.DamageByActor["Raider01"].Total);
        Assert.Equal(3, giant.DamageByActor["Raider01"].Hits);
        Assert.Equal(100, giant.DamageByActor["Raider02"].Total);
        Assert.Equal(2, giant.DamageByActor["Raider02"].Hits);
        Assert.Equal(150, giant.TankingByDefender["Raider01"].Total);
        Assert.Equal(0, giant.TankingByDefender["Raider02"].Total); // the dodge
        Assert.Equal(1, giant.TankingByDefender["Raider02"].Hits);

        var drake = session.Fights.Fights[1];
        Assert.Equal("A shadow drake", drake.Name);
        Assert.False(drake.Closed); // still active at end of file
        Assert.Equal(500, drake.DamageTotal);   // 400 + 60 + 40
        Assert.Equal(400, drake.DamageByActor["Raider01"].Total);
        Assert.Equal(60, drake.DamageByActor["Xobatik"].Total);
        Assert.Equal(40, drake.DamageByActor["Kizant"].Total); // "You" resolved to log owner

        // Identity learned from the log.
        Assert.True(session.Identity.IsVerifiedPlayer("Raider01"));
        Assert.True(session.Identity.IsVerifiedPlayer("Raider02"));
        Assert.True(session.Identity.IsVerifiedPlayer("Raider03"));
        Assert.True(session.Identity.IsVerifiedPlayer("Kizant"));
        Assert.Equal("Raider02", session.Identity.OwnerOf("Xobatik"));
        Assert.True(session.Identity.IsDefinitelyNpc("An ice giant"));

        // Grouping: the 200 s break splits the pulls.
        var groups = FightTracker.Group(session.Fights.Fights);
        Assert.Equal(2, groups.Count);
        Assert.Equal("An ice giant", Assert.Single(groups[0]).Name);
        Assert.Equal("A shadow drake", Assert.Single(groups[1]).Name);

        // Record store range query: fight-1 window contains its 8 combat records.
        var window = session.Records.Range(T0.AddSeconds(2), T0.AddSeconds(8)).ToList();
        Assert.Equal(8, window.Count);
        Assert.Contains(window, r => r.Event is DeathEvent { Victim: "An ice giant" });
    }

    [Fact]
    public async Task SyntheticRaidLogReplaysWithPlausibleFightState()
    {
        var path = Path.Combine(_dir, "eqlog_Test_server.txt");
        new SyntheticLogGenerator(seed: 11, playerCount: 20, start: T0).WriteFile(path, 300_000);

        var session = new Session(path, ingestOptions: new IngestOptions { Follow = false });
        await session.RunAsync(CancellationToken.None);

        Assert.Equal(0, session.Ingestion.MalformedLines);
        Assert.True(session.Fights.Fights.Count > 3, $"expected several fights, got {session.Fights.Fights.Count}");
        Assert.All(session.Fights.Fights, f => Assert.True(f.HasDamage || f.TauntCount > 0));
        Assert.Contains(session.Fights.Fights, f => f.Dead);
    }
}
