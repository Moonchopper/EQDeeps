using EQDeeps.Core.Events;
using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// A cast line says a spell STARTED and carries no numbers; the damage or heal
/// arrives later as its own record. Pairing the two is what lets the timeline
/// size a mark by what the cast actually did, and these pin down where the
/// credit stops: a recast closes the window early, and an unpaired cast keeps
/// a null amount rather than being reported as zero.
/// </summary>
public class TimelineCastAmountTests
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);

    private readonly RecordStore _records = new();
    private readonly FightTracker _tracker;

    public TimelineCastAmountTests()
    {
        var identity = new IdentityRegistry();
        identity.AddVerifiedPlayer("Kizant");
        identity.AddVerifiedPlayer("Healer");
        _tracker = new FightTracker(identity);
    }

    private void Add(int t, GameEvent evt)
    {
        var timestamp = T0.AddSeconds(t);
        _records.Append(timestamp, evt);
        _tracker.Process(timestamp, evt);
    }

    private TimelineItem Cast(string spell, IReadOnlyList<TimelineItem> items) =>
        items.Single(i => i.Kind == TimelineItemKind.Cast && i.Label == spell);

    // An explicit range rather than the fight scope: these scenarios are about
    // the cast→landing join, and a fight-derived scope would clip casts that
    // happen to start before the first damage lands.
    private IReadOnlyList<TimelineItem> Build() =>
        TimelineBuilder.Build(
            _records, _tracker, "Kizant",
            new QueryScope { TimeRanges = [new TimeRange(T0, T0.AddSeconds(120))] }).Items;

    [Fact]
    public void ACastIsCreditedWithWhatItLanded()
    {
        Add(0, new DamageEvent("Kizant", "A giant", 10, DamageKind.Melee, "Crushes"));
        Add(1, new CastEvent("Kizant", "Ice Comet", CastKind.Begin));
        Add(4, new DamageEvent("Kizant", "A giant", 5000, DamageKind.DirectDamage, "Ice Comet"));

        Add(6, new CastEvent("Healer", "Superior Healing", CastKind.Begin));
        Add(8, new HealEvent("Healer", "Kizant", 1200, 1500, OverTime: false, "Superior Healing"));

        var items = Build();

        var nuke = Cast("Ice Comet", items);
        Assert.Equal(5000, nuke.Amount);
        Assert.Equal(TimelineEffect.Damage, nuke.Effect);

        var heal = Cast("Superior Healing", items);
        Assert.Equal(1200, heal.Amount);
        Assert.Equal(TimelineEffect.Heal, heal.Effect);
    }

    [Fact]
    public void AMultiTargetCastSumsEveryTargetItHit()
    {
        Add(1, new CastEvent("Kizant", "Rain of Fire", CastKind.Begin));
        Add(3, new DamageEvent("Kizant", "A giant", 400, DamageKind.DirectDamage, "Rain of Fire"));
        Add(3, new DamageEvent("Kizant", "A troll", 400, DamageKind.DirectDamage, "Rain of Fire"));
        Add(4, new DamageEvent("Kizant", "An orc", 300, DamageKind.DirectDamage, "Rain of Fire"));

        Assert.Equal(1100, Cast("Rain of Fire", Build()).Amount);
    }

    [Fact]
    public void ARecastClosesThePreviousCastsWindowEarly()
    {
        // Without the early close the first cast would swallow the second's
        // damage too, and the mark would read twice its real size.
        Add(1, new CastEvent("Kizant", "Shock of Lightning", CastKind.Begin));
        Add(3, new DamageEvent("Kizant", "A giant", 500, DamageKind.DirectDamage, "Shock of Lightning"));
        Add(5, new CastEvent("Kizant", "Shock of Lightning", CastKind.Begin));
        Add(7, new DamageEvent("Kizant", "A giant", 900, DamageKind.DirectDamage, "Shock of Lightning"));

        var casts = Build()
            .Where(i => i.Kind == TimelineItemKind.Cast && i.Label == "Shock of Lightning")
            .OrderBy(i => i.Start)
            .ToList();

        Assert.Equal(2, casts.Count);
        Assert.Equal(500, casts[0].Amount);
        Assert.Equal(900, casts[1].Amount);
    }

    [Fact]
    public void AnUnpairedCastKeepsNoAmountRatherThanZero()
    {
        // Nothing landed under this name: a buff, a resist, or a spell whose
        // result the parser does not attribute. Zero would claim it did
        // nothing, which is a different statement from "not known".
        Add(1, new CastEvent("Kizant", "Spirit of Wolf", CastKind.Begin));
        Add(3, new DamageEvent("Kizant", "A giant", 700, DamageKind.DirectDamage, "Ice Comet"));

        var buff = Cast("Spirit of Wolf", Build());
        Assert.Null(buff.Amount);
        Assert.Equal(TimelineEffect.None, buff.Effect);
    }

    [Fact]
    public void DamageLandingLongAfterTheCastIsNotCredited()
    {
        // A damage-over-time keeps ticking well past the window. Crediting the
        // whole lifetime to the cast would make a cheap DoT out-size a nuke.
        Add(1, new CastEvent("Kizant", "Pyre of Marr", CastKind.Begin));
        Add(3, new DamageEvent("Kizant", "A giant", 400, DamageKind.DamageOverTime, "Pyre of Marr"));
        Add(60, new DamageEvent("Kizant", "A giant", 400, DamageKind.DamageOverTime, "Pyre of Marr"));

        Assert.Equal(400, Cast("Pyre of Marr", Build()).Amount);
    }
}
