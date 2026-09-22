using EQDeeps.Core.Achievements;
using EQDeeps.Core.Maps;
using EQDeeps.Core.Reference;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// A hand-built toy world (F35, ADR-023 Decisions 4–6): 3 hunting zones and 1 city, 2 races. Every
/// expected number below is computed by hand in the comment beside its assertion, per CLAUDE.md §8 —
/// nothing here is a value copied out of a first run of <see cref="SlayerAtlas"/> or
/// <see cref="SlayerHunting"/> itself.
/// </summary>
public class SlayerHuntingTests
{
    private static NpcSpawnZone Zone(string shortName, string longName, int spawnPoints) =>
        new(shortName, longName, spawnPoints, []);

    private static NpcDetail Listing(
        int id, string name, string? race, int? level, int? respawnSeconds, int? spawnChance,
        IReadOnlyList<NpcFactionHit> factionHits, params NpcSpawnZone[] zones) =>
        new(id, name, level, null, null, null, race, null, null, respawnSeconds, null, null, [], [], zones, spawnChance, factionHits);

    // shortname\tdisplay\tsource\tera\terasource\tids\tcity — the same 7-column shape ZoneTableTests
    // uses; era/erasource/ids are irrelevant here and left blank.
    private static string ZoneRow(string shortName, string display, bool city = false) =>
        string.Join('\t', shortName, display, "curated", "", "", "", city ? "city" : "");

    private static (SlayerAtlas Atlas, HuntQuery Query) BuildWorld()
    {
        // --- "qeytoy1" / Toy West Plains: not a city. Exercises SpawnChance 50, a null chance
        // defaulting to 100, a 30s respawn floored to the 60s minimum, case-insensitive race
        // matching ("kobold" vs the query's "Kobold"), a listing with no race, a listing with no
        // respawn, and a (listing, zone) pair with 0 spawn points — none of the last three can be
        // ranked, and none of them may appear in the atlas at all.
        var grunt = Listing(1, "Kobold Grunt", "kobold", 10, 600, 50, [],
            Zone("qeytoy1", "Toy West Plains", 4),
            Zone("emptyzone", "Empty Hollow", 0)); // spawnPoints 0: "listed nowhere in particular"
        var elite = Listing(2, "Kobold Elite", "Kobold", 12, 30, null, [],
            Zone("qeytoy1", "Toy West Plains", 2));
        var mysteryBlob = Listing(9, "Mystery Blob", null, 5, 100, 100, [],
            Zone("qeytoy1", "Toy West Plains", 1)); // no race: cannot be ranked
        var ghostThing = Listing(10, "Ghost Thing", "Kobold", 7, null, 100, [],
            Zone("qeytoy1", "Toy West Plains", 1)); // no respawn: cannot be ranked

        // --- "gnollden" / Gnoll's Den: not a city. Four Gnoll listings; the level cap (20, set on
        // the query below) drops the over-level Elder and the level-less Whelp at ranking time, but
        // the atlas itself carries all four — the cap is a HuntQuery concern, not an atlas one.
        var runt = Listing(3, "Gnoll Runt", "Gnoll", 15, 120, 100,
            [new NpcFactionHit("Wolves of the North", -20)], Zone("gnollden", "Gnoll's Den", 3));
        var brute = Listing(4, "Gnoll Brute", "Gnoll", 18, 60, 100,
            [new NpcFactionHit("Wolves of the North", -2)], Zone("gnollden", "Gnoll's Den", 1));
        var elder = Listing(5, "Gnoll Elder", "Gnoll", 25, 60, 100,
            [new NpcFactionHit("Wolves of the North", -100)], Zone("gnollden", "Gnoll's Den", 5));
        var whelp = Listing(6, "Gnoll Whelp", "Gnoll", null, 60, 100,
            [new NpcFactionHit("Wolves of the North", -5)], Zone("gnollden", "Gnoll's Den", 2));

        // --- "krakden" / (no table row — falls back to the site's own longName, "Krakden Wilds",
        // and reads as "not a city"). Two Kobold listings whose combined faction hits exercise all
        // three bands of the faction-effect ordering rule: a protected loss, a protected gain, and
        // an unprotected effect.
        var shaman = Listing(7, "Kobold Shaman", "Kobold", 8, 200, 100,
            [new NpcFactionHit("Kelethin", 3)], Zone("krakden", "Krakden Wilds", 2));
        var witchDoctor = Listing(8, "Kobold Witch Doctor", "Kobold", 9, 3600, 100,
            [new NpcFactionHit("Wolves of the North", -1), new NpcFactionHit("Crushbone", 10)],
            Zone("krakden", "Krakden Wilds", 1));

        // --- "somecity" / Some City: a city. One countable Kobold, so it costs a CitiesLeftOut.
        var guard = Listing(11, "Kobold Guard", "Kobold", 10, 100, 100, [],
            Zone("somecity", "Some City", 3));

        var zones = ZoneTable.Parse(string.Join('\n',
        [
            ZoneRow("qeytoy1", "Toy West Plains"),
            ZoneRow("gnollden", "Gnoll's Den"),
            ZoneRow("somecity", "Some City", city: true),
            // "krakden" is deliberately absent — see the comment above.
        ]));

        var atlas = SlayerAtlas.Build(
            [grunt, elite, mysteryBlob, ghostThing, runt, brute, elder, whelp, shaman, witchDoctor, guard],
            zones);

        var query = new HuntQuery(
            Races: ["Kobold", "Gnoll"],
            MaxLevel: 20,
            Remaining: 100,
            Protected:
            [
                // UnlockEarned is deliberately true here despite Gnoll's Den still costing this
                // faction below — Decision 5 says the flag is shown, never used to excuse a loss.
                new ProtectedFaction("Wolves of the North", FactionNames.Key("Wolves of the North"), UnlockEarned: true, Unlocks: ["Racial Diversity"]),
                new ProtectedFaction("Kelethin", FactionNames.Key("Kelethin"), UnlockEarned: false, Unlocks: ["Racial Diversity"]),
            ],
            Standings:
            [
                new FactionStanding(1, "Wolves of the North", -1990),
                new FactionStanding(2, "Kelethin", 100),
            ]);

        return (atlas, query);
    }

    [Fact]
    public void AtlasGroupsByZoneShortNameAndDropsWhatCannotBeRanked()
    {
        var (atlas, _) = BuildWorld();

        Assert.Equal(4, atlas.Zones.Count);

        var toyWestPlains = atlas.Zones.Single(z => z.ShortName == "qeytoy1");
        Assert.Equal("Toy West Plains", toyWestPlains.Name);
        Assert.False(toyWestPlains.City);
        // Only the Grunt and the Elite: Mystery Blob (no race) and Ghost Thing (no respawn) are
        // listed at this same zone but cannot be ranked.
        Assert.Equal([1, 2], toyWestPlains.Mobs.Select(m => m.Id).OrderBy(id => id));

        var grunt = toyWestPlains.Mobs.Single(m => m.Id == 1);
        Assert.Equal(50, grunt.SpawnChance);
        Assert.Equal(600, grunt.RespawnSeconds); // atlas does not floor respawn — that is a ranking-time concern
        var elite = toyWestPlains.Mobs.Single(m => m.Id == 2);
        Assert.Equal(100, elite.SpawnChance); // null on the listing -> 100

        // The Grunt's second zone row, "emptyzone", has 0 spawn points and must not surface at all.
        Assert.DoesNotContain(atlas.Zones, z => z.ShortName == "emptyzone");

        var gnollsDen = atlas.Zones.Single(z => z.ShortName == "gnollden");
        Assert.Equal("Gnoll's Den", gnollsDen.Name);
        // All four Gnoll listings are placed here; the level cap is not an atlas-time filter.
        Assert.Equal(4, gnollsDen.Mobs.Count);

        // Not in the zone table at all: falls back to the site's own longName and is not a city.
        var krakdenWilds = atlas.Zones.Single(z => z.ShortName == "krakden");
        Assert.Equal("Krakden Wilds", krakdenWilds.Name);
        Assert.False(krakdenWilds.City);

        var someCity = atlas.Zones.Single(z => z.ShortName == "somecity");
        Assert.True(someCity.City);

        // Neither the raceless nor the respawnless listing appears anywhere in the atlas.
        var everyId = atlas.Zones.SelectMany(z => z.Mobs).Select(m => m.Id).ToHashSet();
        Assert.DoesNotContain(9, everyId);
        Assert.DoesNotContain(10, everyId);
    }

    [Fact]
    public void RanksZonesByYieldFactionCostAndTheCityRule()
    {
        var (atlas, query) = BuildWorld();
        var result = SlayerHunting.Rank(atlas, query);

        // "somecity" has one countable mob (a level-10 Kobold Guard, under the cap of 20), so it
        // costs exactly one CitiesLeftOut and is absent from Zones — never listed, per Decision 6.
        Assert.Equal(1, result.CitiesLeftOut);
        Assert.DoesNotContain(result.Zones, z => z.ShortName == "somecity");

        // Recommended first (there is exactly one), then the two not-recommended zones ordered by
        // how little protected faction they cost — Krakden Wilds (a hair of a loss) before Gnoll's
        // Den (a real one) — even though Gnoll's Den would out-produce it raw (150/hr vs 37/hr).
        Assert.Equal(["Toy West Plains", "Krakden Wilds", "Gnoll's Den"], result.Zones.Select(z => z.Name));

        var toyWestPlains = result.Zones[0];
        var krakdenWilds = result.Zones[1];
        var gnollsDen = result.Zones[2];

        // --- Toy West Plains ---
        Assert.True(toyWestPlains.Recommended);
        Assert.Equal(0.0, toyWestPlains.ProtectedLossPerKill);
        // Grunt: 4 spawn points x (50/100) chance x 3600 / 600s respawn = 4 x 0.5 x 6 = 12/hr.
        // Elite: 2 spawn points x (100/100, null->100) x 3600 / max(30, 60)s = 2 x 1 x 60 = 120/hr.
        // Zone: 12 + 120 = 132/hr. (The Grunt's race is written "kobold" against the query's
        // "Kobold" — it still counts, so the match is case-insensitive.)
        Assert.Equal(132.0, toyWestPlains.PerHour, 6);
        Assert.Equal(6, toyWestPlains.SpawnPoints); // 4 + 2
        Assert.Equal(10, toyWestPlains.MinLevel);   // min(10, 12)
        Assert.Equal(12, toyWestPlains.MaxLevel);   // max(MaxLevel ?? Level) over 10, 12 — neither lists its own MaxLevel
        Assert.Equal(["Kobold Elite", "Kobold Grunt"], toyWestPlains.Mobs.Select(m => m.Name)); // PerHour desc: 120 before 12
        Assert.Equal(120.0, toyWestPlains.Mobs[0].PerHour, 6);
        Assert.Equal(12.0, toyWestPlains.Mobs[1].PerHour, 6);
        // Neither mob has a faction hit, so there is nothing to show here at all — the direct guard
        // that a factionless listing contributes no faction effect, not merely no protected one.
        Assert.Empty(toyWestPlains.Factions);

        // --- Gnoll's Den ---
        Assert.False(gnollsDen.Recommended);
        // The level cap (20) drops the level-25 Elder outright and drops the level-less Whelp too —
        // a null level cannot be shown to satisfy a cap — leaving the Runt and the Brute to count.
        // Runt: 3 x (100/100) x 3600/120 = 3 x 30 = 90/hr. Brute: 1 x (100/100) x 3600/60 = 60/hr.
        Assert.Equal(150.0, gnollsDen.PerHour, 6); // 90 + 60
        Assert.Equal(4, gnollsDen.SpawnPoints);    // 3 + 1
        Assert.Equal(15, gnollsDen.MinLevel);      // min(15, 18) — the Elder's 25 and the Whelp's null never enter
        Assert.Equal(18, gnollsDen.MaxLevel);
        Assert.Equal(["Gnoll Runt", "Gnoll Brute"], gnollsDen.Mobs.Select(m => m.Name)); // 90 before 60

        var wolvesGnollsDen = Assert.Single(gnollsDen.Factions);
        Assert.Equal("Wolves of the North", wolvesGnollsDen.Faction);
        // Supply-weighted mean, not a plain average of the two deltas: (90 x -20 + 60 x -2) / 150 =
        // (-1800 - 120) / 150 = -1920 / 150 = -12.8. A plain average of -20 and -2 would read -11 —
        // the two disagree, which is the point: the Brute's smaller supply must not pull the number
        // as hard as the Runt's larger one. (The Elder's -100 and the Whelp's -5 never enter this
        // sum at all — both were dropped by the level cap before the faction pass ever runs.)
        Assert.Equal(-12.8, wolvesGnollsDen.PerKill, 6);
        Assert.NotEqual(-11.0, wolvesGnollsDen.PerKill, 6); // guards against a plain-average implementation
        Assert.True(wolvesGnollsDen.Protected);
        // UnlockEarned is true (set on the query above) and changes nothing: the zone is still not
        // recommended and still carries the loss (ADR-023 Decision 5 — shown, never used to soften).
        Assert.True(wolvesGnollsDen.UnlockEarned);
        Assert.Equal(-1990, wolvesGnollsDen.Standing);
        // Projected = clamp(-1990 + round(-12.8 x 100), -2000, 2000)
        //           = clamp(-1990 + round(-1280), ...) = clamp(-1990 - 1280, ...)
        //           = clamp(-3270, -2000, 2000) = -2000 — the clamp at the floor.
        Assert.Equal(-2000, wolvesGnollsDen.Projected);
        // The zone's one loss, expressed positive.
        Assert.Equal(12.8, gnollsDen.ProtectedLossPerKill, 6);

        // --- Krakden Wilds ---
        Assert.False(krakdenWilds.Recommended);
        // Shaman: 2 x (100/100) x 3600/200 = 2 x 18 = 36/hr. Witch Doctor: 1 x (100/100) x
        // 3600/3600 = 1/hr.
        Assert.Equal(37.0, krakdenWilds.PerHour, 6); // 36 + 1
        Assert.Equal(["Kobold Shaman", "Kobold Witch Doctor"], krakdenWilds.Mobs.Select(m => m.Name));

        // Three effects, in order: a protected loss, then a protected gain, then everything else —
        // exercising the full three-band ordering rule, not just the tiebreak at the end of it.
        Assert.Equal(3, krakdenWilds.Factions.Count);
        var wolvesKrakden = krakdenWilds.Factions[0];
        var kelethinKrakden = krakdenWilds.Factions[1];
        var crushboneKrakden = krakdenWilds.Factions[2];

        // Loss first: only the Witch Doctor hits Wolves of the North, at -1: (1 x -1) / 37 = -1/37
        // ~= -0.027027.
        Assert.Equal("Wolves of the North", wolvesKrakden.Faction);
        Assert.Equal(-1.0 / 37.0, wolvesKrakden.PerKill, 6);
        Assert.True(wolvesKrakden.Protected);
        Assert.Equal(-1990, wolvesKrakden.Standing);
        // Projected = clamp(-1990 + round(-1/37 x 100), ...) = clamp(-1990 + round(-2.7027...), ...)
        //           = clamp(-1990 - 3, ...) = -1993 — not a clamp, just an ordinary move.
        Assert.Equal(-1993, wolvesKrakden.Projected);

        // Gain second: only the Shaman hits Kelethin, at +3: (36 x 3) / 37 = 108/37 ~= 2.918919.
        Assert.Equal("Kelethin", kelethinKrakden.Faction);
        Assert.Equal(108.0 / 37.0, kelethinKrakden.PerKill, 6);
        Assert.True(kelethinKrakden.Protected);
        Assert.Equal(100, kelethinKrakden.Standing);
        // Projected = clamp(100 + round(108/37 x 100), ...) = clamp(100 + round(291.8919...), ...)
        //           = clamp(100 + 292, ...) = 392 — a gain, nowhere near either clamp.
        Assert.Equal(392, kelethinKrakden.Projected);

        // Everything else last: Crushbone is not a protected faction, only the Witch Doctor hits
        // it (at +10), and nobody's standing with it is in the export at all.
        // (1 x 10) / 37 = 10/37 ~= 0.270270.
        Assert.Equal("Crushbone", crushboneKrakden.Faction);
        Assert.Equal(10.0 / 37.0, crushboneKrakden.PerKill, 6);
        Assert.False(crushboneKrakden.Protected);
        Assert.False(crushboneKrakden.UnlockEarned);
        Assert.Null(crushboneKrakden.Standing); // no standing on the export -> no projection either
        Assert.Null(crushboneKrakden.Projected);

        // Only the Wolves loss counts here — Kelethin's gain does not offset it, and Crushbone was
        // never protected to begin with: -(-1/37) = 1/37 ~= 0.027027.
        Assert.Equal(1.0 / 37.0, krakdenWilds.ProtectedLossPerKill, 6);
    }
}
