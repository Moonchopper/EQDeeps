using EQDeeps.Core.Maps;
using EQDeeps.Core.Reference;

namespace EQDeeps.Core.Achievements;

/// <summary>
/// One reference listing's presence at one zone — one row per (listing, <see cref="NpcSpawnZone"/>)
/// pair, which is why a listing that stands in several zones contributes several of these. Carries
/// everything <see cref="SlayerHunting"/> needs to weigh it: what it takes to find (spawn points,
/// chance, respawn) and what killing it costs (<see cref="FactionHits"/>).
/// </summary>
public sealed record AtlasMob(
    int Id,
    string Name,
    string Race,
    int? Level,
    int? MaxLevel,
    int SpawnPoints,
    int RespawnSeconds,
    int SpawnChance,
    IReadOnlyList<NpcFactionHit> FactionHits);

/// <summary>One zone's roster of atlas mobs, named the way the log speaks it and flagged if it is one of the 23 player cities (F35, ADR-023 Decision 6).</summary>
public sealed record AtlasZone(string ShortName, string Name, bool City, IReadOnlyList<AtlasMob> Mobs);

/// <summary>
/// Race → zone → supply, built once over every reference listing the app has cached (F35, ADR-023
/// Decision 4). Pure and IO-free by design: <see cref="Build"/> takes listings someone else already
/// read off disk and a zone table already loaded, allocates its own result, and reads nothing twice —
/// it is derived, never stored (ADR-023 Decision 7), so there is nothing here for a caller to cache
/// against going stale.
/// </summary>
public sealed class SlayerAtlas
{
    private SlayerAtlas(IReadOnlyList<AtlasZone> zones) => Zones = zones;

    public IReadOnlyList<AtlasZone> Zones { get; }

    /// <summary>
    /// Groups every listing's spawn-zone rows by zone. A listing missing what the ranking needs to
    /// weigh it — no race, or no usable respawn — is left out entirely rather than kept with a
    /// guessed value, and a spawn-zone row with no spawn points is dropped the same way (the shard
    /// parser already defaults <c>spawnPoints</c> from the location count, so a zero here is a real
    /// "not placed anywhere in particular", not a missing field).
    /// </summary>
    public static SlayerAtlas Build(IEnumerable<NpcDetail> listings, ZoneTable zones)
    {
        var order = new List<string>();
        var mobsByZone = new Dictionary<string, List<AtlasMob>>(StringComparer.OrdinalIgnoreCase);
        var fallbackNameByZone = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var listing in listings)
        {
            var race = listing.Race;
            if (string.IsNullOrWhiteSpace(race))
            {
                continue; // no race, so nothing a hunt query could ever ask for
            }

            var respawnSeconds = listing.RespawnSeconds;
            if (respawnSeconds is null || respawnSeconds.Value <= 0)
            {
                continue; // no usable respawn: the supply formula has nothing to divide by
            }

            foreach (var zone in listing.Zones)
            {
                if (zone.SpawnPoints <= 0)
                {
                    continue;
                }

                var mob = new AtlasMob(
                    listing.Id,
                    listing.Name,
                    race,
                    listing.Level,
                    listing.MaxLevel,
                    zone.SpawnPoints,
                    respawnSeconds.Value,
                    listing.SpawnChance ?? 100,
                    listing.FactionHits);

                // Filed under the listing's own zone short name — never the shard number that id
                // implies. ADR-020 Decision 6 is explicit that the site's "id / 1000" numbering is
                // a convention, not a contract, and NpcReferenceStore.RosterAsync already treats it
                // as unverified, using the id only to find a shard and then trusting each row's own
                // "zones" field for where it actually stands. The atlas does the same: a listing can
                // be shelved in the wrong shard and still file correctly here.
                if (!mobsByZone.TryGetValue(zone.ShortName, out var list))
                {
                    list = [];
                    mobsByZone[zone.ShortName] = list;
                    fallbackNameByZone[zone.ShortName] = zone.LongName;
                    order.Add(zone.ShortName);
                }

                list.Add(mob);
            }
        }

        var result = new List<AtlasZone>(order.Count);
        foreach (var shortName in order)
        {
            var entry = zones.EntryFor(shortName);

            // A short name the table does not know is not an error — the table is deliberately
            // incomplete (ZoneTable's own doc comment) — so the zone still gets a name (the site's
            // own longName) and reads as "not a city" rather than vanishing or being guessed at.
            var name = entry?.DisplayName ?? fallbackNameByZone[shortName];
            var city = entry?.City ?? false;
            result.Add(new AtlasZone(shortName, name, city, mobsByZone[shortName]));
        }

        return new SlayerAtlas(result);
    }
}
