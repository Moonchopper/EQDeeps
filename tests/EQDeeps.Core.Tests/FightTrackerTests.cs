using EQDeeps.Core.Events;
using EQDeeps.Core.Sessions;
using Xunit;

namespace EQDeeps.Core.Tests;

public class FightTrackerTests
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);

    private readonly IdentityRegistry _identity = new();
    private readonly FightTracker _tracker;

    public FightTrackerTests()
    {
        _identity.AddVerifiedPlayer("Raider01");
        _identity.AddVerifiedPlayer("Raider02");
        _tracker = new FightTracker(_identity);
    }

    private void Melee(int t, string attacker, string defender, uint amount) =>
        _tracker.Process(T0.AddSeconds(t), new DamageEvent(attacker, defender, amount, DamageKind.Melee, "Crushes"));

    [Fact]
    public void PlayerAttackingNpcCreatesFightAndAccumulatesBothSides()
    {
        Melee(0, "Raider01", "An ice giant", 100);
        Melee(1, "Raider01", "An ice giant", 200);
        Melee(2, "An ice giant", "Raider02", 150);
        Melee(3, "Raider02", "An ice giant", 50);

        var fight = Assert.Single(_tracker.Fights);
        Assert.Equal("An ice giant", fight.Name);
        Assert.Equal(T0, fight.BeginTime);
        Assert.Equal(T0.AddSeconds(3), fight.LastDamageTime);
        Assert.Equal(350, fight.DamageTotal);
        Assert.Equal(150, fight.TankingTotal);
        Assert.Equal(300, fight.DamageByActor["Raider01"].Total);
        Assert.Equal(2, fight.DamageByActor["Raider01"].Hits);
        Assert.Equal(50, fight.DamageByActor["Raider02"].Total);
        Assert.Equal(150, fight.TankingByDefender["Raider02"].Total);
        Assert.False(fight.Closed);
        Assert.Equal(4, fight.Seconds.Count);
        Assert.Equal(100, fight.Seconds[T0].Damage);
        Assert.Equal(150, fight.Seconds[T0.AddSeconds(2)].Tanking);
    }

    [Fact]
    public void DeathClosesFightAndSameNameLaterIsANewFight()
    {
        Melee(0, "Raider01", "An ice giant", 100);
        _tracker.Process(T0.AddSeconds(1), new DeathEvent("An ice giant", "Raider01"));
        Melee(5, "Raider01", "An ice giant", 200);

        Assert.Equal(2, _tracker.Fights.Count);
        Assert.True(_tracker.Fights[0].Dead);
        Assert.True(_tracker.Fights[0].Closed);
        Assert.Equal(100, _tracker.Fights[0].DamageTotal);
        Assert.False(_tracker.Fights[1].Closed);
        Assert.Equal(200, _tracker.Fights[1].DamageTotal);
        Assert.NotEqual(_tracker.Fights[0].Id, _tracker.Fights[1].Id);
    }

    [Fact]
    public void ThirtySecondCombatInactivityClosesAFightWithDamage()
    {
        Melee(0, "Raider01", "An ice giant", 100);
        Melee(29, "Raider01", "An ice giant", 100); // 29 s gap: same fight
        Melee(60, "Raider01", "An ice giant", 100); // 31 s gap: new fight

        Assert.Equal(2, _tracker.Fights.Count);
        Assert.Equal(200, _tracker.Fights[0].DamageTotal);
        Assert.Equal(T0.AddSeconds(29), _tracker.Fights[0].LastDamageTime);
        Assert.Equal(100, _tracker.Fights[1].DamageTotal);
    }

    [Fact]
    public void TauntOnlyFightSurvivesToSixtySeconds()
    {
        _tracker.Process(T0, new TauntEvent("Raider01", "Doomshade", Success: true));
        _tracker.Process(T0.AddSeconds(45), new TauntEvent("Raider01", "Doomshade", Success: true));

        var fight = Assert.Single(_tracker.Fights);
        Assert.False(fight.Closed); // 45 s idle but no damage: still open
        Assert.Equal(2, fight.TauntCount);

        _tracker.Process(T0.AddSeconds(45 + 61), new TauntEvent("Raider01", "Doomshade", Success: true));
        Assert.Equal(2, _tracker.Fights.Count); // 61 s exceeds the hard cap
    }

    [Fact]
    public void NpcVersusNpcAndPlayerVersusPlayerAreIgnored()
    {
        Melee(0, "An ice giant", "A shadow drake", 500);
        _tracker.Process(T0.AddSeconds(1),
            new DamageEvent("Raider01", "Raider02", 25, DamageKind.DamageShield, null));

        Assert.Empty(_tracker.Fights);
    }

    [Fact]
    public void UnknownSingleWordDefenderIsAssumedNpcUntilVerified()
    {
        Melee(0, "Raider01", "Falsehood", 100);
        Assert.Single(_tracker.Fights);

        // The name turns out to be a player (e.g. chats in guild) — the phantom
        // fight is deleted on the next processed record.
        _identity.AddVerifiedPlayer("Falsehood");
        Melee(1, "Raider01", "An ice giant", 50);

        var fight = Assert.Single(_tracker.Fights);
        Assert.Equal("An ice giant", fight.Name);
    }

    [Fact]
    public void UnknownAttackerAgainstKnownNpcCountsAsPlayersSide()
    {
        // Unverified players and unmapped pets attack NPCs constantly; the fight
        // must not wait for verification.
        Melee(0, "Xobatik", "An ice giant", 100);

        var fight = Assert.Single(_tracker.Fights);
        Assert.Equal("An ice giant", fight.Name);
        Assert.Equal(100, fight.DamageByActor["Xobatik"].Total);
    }

    [Fact]
    public void TwoUnknownsDoNotCreateAFight()
    {
        Melee(0, "Falsehood", "Mystery", 100);
        Assert.Empty(_tracker.Fights);
    }

    [Fact]
    public void UnknownSourceDamageDoesNotCreateAFight()
    {
        _tracker.Process(T0, new DamageEvent(null, "Raider01", 2700, DamageKind.DamageShield, null));
        Assert.Empty(_tracker.Fights);
    }

    [Fact]
    public void ZoneTransitionClosesActiveFights()
    {
        Melee(0, "Raider01", "An ice giant", 100);
        _tracker.Process(T0.AddSeconds(1), new ZoneEvent(null));

        var fight = Assert.Single(_tracker.Fights);
        Assert.True(fight.Closed);
        Assert.False(fight.Dead);
    }

    [Fact]
    public void SpellAsAttackerAttributesToCasterSideWithinWindow()
    {
        _tracker.Process(T0, new CastEvent("Raider01", "Wisp Explosion", CastKind.Begin));
        _tracker.Process(T0.AddSeconds(5),
            new DamageEvent("Wisp Explosion", "An ice giant", 500, DamageKind.Other, "Wisp Explosion", AttackerIsSpell: true));

        var fight = Assert.Single(_tracker.Fights);
        Assert.Equal(500, fight.DamageTotal);
        Assert.Equal(500, fight.DamageByActor["Wisp Explosion"].Total);
    }

    [Fact]
    public void SpellAsAttackerWithoutRecentCastIsIgnored()
    {
        _tracker.Process(T0,
            new DamageEvent("Wisp Explosion", "An ice giant", 500, DamageKind.Other, "Wisp Explosion", AttackerIsSpell: true));

        Assert.Empty(_tracker.Fights);
    }

    [Fact]
    public void PetOwnerAnnotationMapsPetAndKeepsDamageUnderPetName()
    {
        Melee(0, "Raider01", "An ice giant", 10);
        _tracker.Process(T0.AddSeconds(1),
            new DamageEvent("Lobekn", "An ice giant", 311, DamageKind.DirectDamage, "Earthquake", AttackerOwner: "Bulron"));

        var fight = Assert.Single(_tracker.Fights);
        Assert.Equal(311, fight.DamageByActor["Lobekn"].Total);
        Assert.Equal("Bulron", _identity.OwnerOf("Lobekn"));
    }

    [Fact]
    public void PlayerKillMarksVictimAsKnownNpcButPetDeathsDoNot()
    {
        _tracker.Process(T0, new DeathEvent("Grumbuk", "Raider01"));
        Assert.True(_identity.IsDefinitelyNpc("Grumbuk"));

        _tracker.Process(T0.AddSeconds(1), new DeathEvent("Kizante`s pet", "A rockborn"));
        Assert.False(_identity.IsDefinitelyNpc("Kizante`s pet"));
    }

    [Fact]
    public void GroupingSplitsOnGapsOfAtLeastTheGroupTimeout()
    {
        Melee(0, "Raider01", "An ice giant", 100);
        _tracker.Process(T0.AddSeconds(1), new DeathEvent("An ice giant", "Raider01"));
        Melee(60, "Raider01", "A shadow drake", 100);
        _tracker.Process(T0.AddSeconds(61), new DeathEvent("A shadow drake", "Raider01"));
        Melee(61 + 120, "Raider01", "Doomshade", 100);

        var groups = FightTracker.Group(_tracker.Fights);
        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal("Doomshade", Assert.Single(groups[1]).Name);
    }
}
