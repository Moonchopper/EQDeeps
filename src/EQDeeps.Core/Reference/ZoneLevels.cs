using EQDeeps.Core.Maps;

namespace EQDeeps.Core.Reference;

/// <summary>
/// What level a zone is, as far as who stands there can say: the middle half
/// of the levels the site lists for the zone's NPCs.
/// </summary>
/// <param name="ZoneId">The client zone id the listings fall under.</param>
/// <param name="Name">The zone as the table names it, or null for an id it has no row for.</param>
/// <param name="Maps">Every map short name that draws the place, so a label on any drawing can find its band.</param>
/// <param name="Low">The 25th-percentile listed level.</param>
/// <param name="High">The 75th-percentile listed level.</param>
/// <param name="Listings">How many listings with a level the band was read from.</param>
public sealed record ZoneLevelBand(
    int ZoneId,
    string? Name,
    IReadOnlyList<string> Maps,
    int Low,
    int High,
    int Listings);

/// <summary>
/// Turns the index into a level band per zone, from the listings' ids alone
/// (a shard is a zone; <see cref="NpcReferenceFormat.ShardOf"/>) — the same
/// join <see cref="NpcPlaces"/> makes, run the other way round.
///
/// <para>The band is the interquartile range, not the extremes, and that is
/// the whole trick. Measured on the live index (2026-08-17): every zone has
/// a stray — a level-65 named, a guard, a quest giver — so min–max makes
/// Everfrost "L1–65" and Nektulos "L1–72", which says nothing. The middle
/// half reads like a zone guide: Crushbone 5–14, Blackburrow 7–14, Guk 11–24
/// above and 31–40 below, Sol A 24–30, Mistmoore 27–33, Kedge 37–44, the
/// Hole 46–55, Fear 49–52. Cities come out high (guards at 40–61 are most
/// of what a city lists), which is true of who stands there even if it is
/// not where anyone hunts; the count is carried so a reader can weigh it.
/// Fewer than <paramref name="minListings"/> listings is no band at all —
/// two mobs are not a distribution.</para>
/// </summary>
public static class ZoneLevels
{
    public static IReadOnlyList<ZoneLevelBand> Of(NpcIndex index, ZoneTable table, int minListings = 5)
    {
        var bands = new List<ZoneLevelBand>();
        foreach (var group in index.Entries
                     .Where(e => e.Level is not null)
                     .GroupBy(e => NpcReferenceFormat.ShardOf(e.Id))
                     .OrderBy(g => g.Key))
        {
            var levels = group.Select(e => e.Level!.Value).Order().ToArray();
            if (levels.Length < minListings)
            {
                continue;
            }

            var entries = table.ZonesForId(group.Key);
            bands.Add(new ZoneLevelBand(
                group.Key,
                entries.Count > 0 ? entries[0].DisplayName : null,
                entries.Select(e => e.ShortName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Quartile(levels, 0.25),
                Quartile(levels, 0.75),
                levels.Length));
        }

        return bands;
    }

    /// <summary>The value at a fraction of the way through a sorted list — nearest rank, no interpolation, so it is always a level somebody is.</summary>
    private static int Quartile(int[] sorted, double fraction) =>
        sorted[Math.Min(sorted.Length - 1, (int)(fraction * sorted.Length))];
}
