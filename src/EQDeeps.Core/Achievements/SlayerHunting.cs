namespace EQDeeps.Core.Achievements;

/// <summary>
/// What the player is hunting for, and everything the ranking needs to know about their standing
/// (F35, ADR-023 Decisions 4–5).
/// </summary>
/// <param name="Races">The reference race labels this achievement's creature words resolved to (<see cref="SlayerRaces"/>), matched case-insensitively.</param>
/// <param name="MaxLevel">The player's chosen level cap, or null for no cap — there is no lower bound, ever; see <see cref="SlayerHunting"/>'s remarks.</param>
/// <param name="Remaining">Kills still needed to finish the achievement — the horizon <see cref="HuntFactionEffect.Projected"/> looks ahead over.</param>
/// <param name="Protected">The factions the export itself says this player is working on (<see cref="ProtectedFactions.From"/>).</param>
/// <param name="Standings">The player's actual standings, from <c>/outputfile faction</c> (<see cref="FactionExport"/>).</param>
public sealed record HuntQuery(
    IReadOnlyList<string> Races,
    int? MaxLevel,
    int Remaining,
    IReadOnlyList<ProtectedFaction> Protected,
    IReadOnlyList<FactionStanding> Standings);

/// <summary>One counted mob's contribution to its zone's supply.</summary>
public sealed record HuntMob(
    int Id,
    string Name,
    string Race,
    int? Level,
    int? MaxLevel,
    int SpawnPoints,
    int RespawnSeconds,
    double PerHour);

/// <summary>
/// What hunting a zone does to one faction, supply-weighted across every counted mob that hits it —
/// so a mob nobody can reach in an hour barely moves this even if its own hit is large.
/// </summary>
/// <param name="Standing">The player's current standing from the faction export, or null when the export names no row for this faction.</param>
/// <param name="Projected">
/// Where <paramref name="Standing"/> would land after <see cref="HuntQuery.Remaining"/> more kills at
/// this rate, clamped to the file's ±2000, or null when there is no standing to project from or
/// nothing left to project over.
/// </param>
public sealed record HuntFactionEffect(
    string Faction, double PerKill, bool Protected, bool UnlockEarned, int? Standing, int? Projected);

/// <summary>One zone's verdict: what it is worth, what it costs, and whether it is safe to send someone.</summary>
public sealed record HuntZone(
    string ShortName,
    string Name,
    double PerHour,
    int SpawnPoints,
    int? MinLevel,
    int? MaxLevel,
    bool Recommended,
    double ProtectedLossPerKill,
    IReadOnlyList<HuntMob> Mobs,
    IReadOnlyList<HuntFactionEffect> Factions);

/// <summary>The ranked answer to "where do I hunt these races", and how many cities were left off it.</summary>
public sealed record HuntResult(IReadOnlyList<HuntZone> Zones, int CitiesLeftOut);

/// <summary>
/// Turns a <see cref="SlayerAtlas"/> into a ranked hunting list (F35, ADR-023 Decisions 4–6). Pure:
/// no IO, no clock — the same atlas and query in always give the same result out.
/// </summary>
public static class SlayerHunting
{
    // Below this a faction effect would render as "0 a kill" and add nothing but noise to the row.
    private const double MinDisplayedPerKill = 0.005;

    public static HuntResult Rank(SlayerAtlas atlas, HuntQuery query)
    {
        var races = new HashSet<string>(query.Races, StringComparer.OrdinalIgnoreCase);
        var protectedByKey = query.Protected.ToDictionary(p => p.Key);

        var standingByKey = new Dictionary<string, int>();
        foreach (var standing in query.Standings)
        {
            var key = FactionNames.Key(standing.Name);
            if (!standingByKey.ContainsKey(key))
            {
                standingByKey[key] = standing.Standing; // first-listed wins, file order
            }
        }

        var zones = new List<HuntZone>();
        var citiesLeftOut = 0;

        foreach (var zone in atlas.Zones)
        {
            var counted = zone.Mobs.Where(m => Counts(m, races, query.MaxLevel)).ToList();
            if (counted.Count == 0)
            {
                // Nothing here answers the query — absent either way, and a city with nothing to
                // hunt in it was never a candidate to begin with, so it is not a city "left out".
                continue;
            }

            if (zone.City)
            {
                // ADR-023 Decision 6: a city is never recommended, and here that means never even
                // listed — the omission is made legible by CitiesLeftOut instead.
                citiesLeftOut++;
                continue;
            }

            zones.Add(BuildZone(zone, counted, protectedByKey, standingByKey, query.Remaining));
        }

        // Recommended zones first, ranked by what they yield; the rest ordered by how little of a
        // problem they are before yield gets a say, so a zone that costs less faction always beats
        // one that costs more, however much more it would kill per hour.
        var recommended = zones
            .Where(z => z.Recommended)
            .OrderByDescending(z => z.PerHour)
            .ThenBy(z => z.Name, StringComparer.Ordinal);

        var others = zones
            .Where(z => !z.Recommended)
            .OrderBy(z => z.ProtectedLossPerKill)
            .ThenByDescending(z => z.PerHour)
            .ThenBy(z => z.Name, StringComparer.Ordinal);

        return new HuntResult([.. recommended, .. others], citiesLeftOut);
    }

    /// <summary>
    /// Which mobs count toward a zone's supply. <b>There is no lower level bound, ever:</b> a
    /// trivial kill still counts toward a Slayer achievement on Legends (the owner, 2026-09-20;
    /// ADR-023 Decision 4), so the thickest spawn a character can reach is the best one however
    /// grey it cons, and a starter zone is a perfectly good answer for a level-50 character short a
    /// hundred snakes. A cap only ever removes mobs from consideration — it never adds a floor — and
    /// a mob with no listed level is removed by a cap it cannot be shown to satisfy, which is why it
    /// survives when there is no cap at all but not when one is set.
    /// </summary>
    private static bool Counts(AtlasMob mob, HashSet<string> races, int? maxLevel) =>
        races.Contains(mob.Race) && (maxLevel is null || (mob.Level is { } level && level <= maxLevel));

    private static HuntZone BuildZone(
        AtlasZone zone,
        List<AtlasMob> counted,
        Dictionary<string, ProtectedFaction> protectedByKey,
        Dictionary<string, int> standingByKey,
        int remaining)
    {
        var weighed = counted.Select(m => (Mob: m, PerHour: PerHour(m))).ToList();
        var zonePerHour = weighed.Sum(w => w.PerHour);
        var zoneSpawnPoints = counted.Sum(m => m.SpawnPoints);

        var levels = counted.Where(m => m.Level is not null).Select(m => m.Level!.Value).ToList();
        var minLevel = levels.Count > 0 ? levels.Min() : (int?)null;

        // Every counted mob's better-known level: its own MaxLevel where the site gives one, its
        // single Level otherwise — so a zone's span is not silently narrowed by mobs the site only
        // ever listed at one level.
        var maxLevels = counted
            .Select(m => m.MaxLevel ?? m.Level)
            .Where(l => l is not null)
            .Select(l => l!.Value)
            .ToList();
        var maxLevel = maxLevels.Count > 0 ? maxLevels.Max() : (int?)null;

        var mobs = weighed
            .Select(w => new HuntMob(w.Mob.Id, w.Mob.Name, w.Mob.Race, w.Mob.Level, w.Mob.MaxLevel, w.Mob.SpawnPoints, w.Mob.RespawnSeconds, w.PerHour))
            .OrderByDescending(m => m.PerHour)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ToList();

        var factions = BuildFactionEffects(weighed, zonePerHour, protectedByKey, standingByKey, remaining);

        // The positive cost of hunting here: how much a protected standing would fall per kill,
        // summed over every protected faction this zone loses. A zone that only gains protected
        // standing, or touches none at all, costs nothing and is recommended.
        var protectedLossPerKill = -factions.Where(f => f.Protected && f.PerKill < 0).Sum(f => f.PerKill);

        return new HuntZone(
            zone.ShortName,
            zone.Name,
            zonePerHour,
            zoneSpawnPoints,
            minLevel,
            maxLevel,
            protectedLossPerKill == 0,
            protectedLossPerKill,
            mobs,
            factions);
    }

    private static double PerHour(AtlasMob mob) =>
        mob.SpawnPoints * (mob.SpawnChance / 100.0) * 3600.0 / Math.Max(mob.RespawnSeconds, 60);

    private static IReadOnlyList<HuntFactionEffect> BuildFactionEffects(
        List<(AtlasMob Mob, double PerHour)> weighed,
        double zonePerHour,
        Dictionary<string, ProtectedFaction> protectedByKey,
        Dictionary<string, int> standingByKey,
        int remaining)
    {
        var order = new List<string>();
        var displayName = new Dictionary<string, string>();
        var weightedHit = new Dictionary<string, double>();

        foreach (var (mob, perHour) in weighed)
        {
            foreach (var hit in mob.FactionHits)
            {
                var key = FactionNames.Key(hit.Faction);
                if (!weightedHit.ContainsKey(key))
                {
                    weightedHit[key] = 0;
                    displayName[key] = hit.Faction; // first spelling met, the zone's own mob order
                    order.Add(key);
                }

                weightedHit[key] += perHour * hit.Delta;
            }
        }

        var effects = new List<HuntFactionEffect>();
        foreach (var key in order)
        {
            // A supply-weighted mean, not a plain average of the deltas: a mob nobody will see in an
            // hour must not pull the number as hard as one standing in front of the player constantly.
            var perKill = zonePerHour == 0 ? 0 : weightedHit[key] / zonePerHour;
            if (Math.Abs(perKill) < MinDisplayedPerKill)
            {
                continue;
            }

            var isProtected = protectedByKey.TryGetValue(key, out var protectedFaction);
            var standing = standingByKey.TryGetValue(key, out var s) ? (int?)s : null;

            int? projected = standing is null || remaining <= 0
                ? null
                : Math.Clamp(standing.Value + (int)Math.Round(perKill * remaining), -2000, 2000);

            effects.Add(new HuntFactionEffect(
                displayName[key],
                perKill,
                isProtected,
                // ADR-023 Decision 5: UnlockEarned does not soften the recommendation below — a
                // completed "maximum faction" unlock does not mean the standing itself is safe (six
                // of the owner's forty protected factions disagree, one sitting at 0 with its unlock
                // long complete), so it is carried here to be shown, never consulted to excuse a loss.
                protectedFaction?.UnlockEarned ?? false,
                standing,
                projected));
        }

        return effects
            .OrderBy(FactionSortRank)
            .ThenBy(FactionSortValue)
            .ThenBy(f => f.Faction, StringComparer.Ordinal)
            .ToList();
    }

    // A total order in three bands — protected losses (worst first), then protected gains (best
    // first), then everything else (largest effect first) — so the toy-world test is not at the
    // mercy of a dictionary's own enumeration order (Gotcha 2).
    private static int FactionSortRank(HuntFactionEffect f) =>
        f.Protected && f.PerKill < 0 ? 0 : f.Protected && f.PerKill > 0 ? 1 : 2;

    private static double FactionSortValue(HuntFactionEffect f) => FactionSortRank(f) switch
    {
        0 => f.PerKill,             // most negative first
        1 => -f.PerKill,            // largest gain first
        _ => -Math.Abs(f.PerKill),  // largest magnitude first
    };
}
