using EQDeeps.Core.Maps;

namespace EQDeeps.Core.Reference;

/// <summary>
/// One zone a name is listed in, as far as the site's id scheme and the zone
/// table can say without fetching anything.
/// </summary>
/// <param name="ZoneId">The client zone id the listings' ids fall under.</param>
/// <param name="Name">
/// The zone as the log and the Map view name it, from the zone table; the
/// site's own wording when the table has no row; null when neither knows —
/// a listing filed under an id this build has no place for.
/// </param>
/// <param name="ShortName">
/// The short name the site files the zone under when it lists one, else the
/// place's first map — what a roster is fetched by.
/// </param>
/// <param name="Maps">Every map short name that draws the place, first-listed first, for opening it.</param>
/// <param name="Era">The place's era from the zone table, when it has one.</param>
/// <param name="Levels">Distinct levels the name is listed at there, ascending.</param>
/// <param name="Ids">The listings there, in index order — any one opens the mob in that zone.</param>
public sealed record NpcPlace(
    int ZoneId,
    string? Name,
    string? ShortName,
    IReadOnlyList<string> Maps,
    string? Era,
    IReadOnlyList<int> Levels,
    IReadOnlyList<int> Ids);

/// <summary>
/// Turns a name's listings into the zones they stand in, from their ids alone
/// (<see cref="NpcReferenceFormat"/>: a shard is a zone). "Where is a ghoul"
/// is thirty-three listings across twenty zones; answering it by fetching
/// each stat block would be twenty shard files, and this is none.
///
/// <para>The join is checked as far as it can be without a fetch: a zone id
/// names a place through the zone table, and the site's own zone rows say
/// whether it lists a zone by that short name — or, for a place with several
/// drawings, by that name. Where neither the table nor the site can name the
/// id, the place is kept with no name rather than dropped, so the count of
/// listings still adds up on screen.</para>
/// </summary>
public static class NpcPlaces
{
    public static IReadOnlyList<NpcPlace> Of(IReadOnlyList<NpcIndexEntry> variants, ZoneTable table, NpcIndex index)
    {
        var places = new List<NpcPlace>();
        foreach (var group in variants.GroupBy(v => NpcReferenceFormat.ShardOf(v.Id)).OrderBy(g => g.Key))
        {
            var zoneId = group.Key;
            var entries = table.ZonesForId(zoneId);
            var maps = entries.Select(e => e.ShortName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var tableName = entries.Count > 0 ? entries[0].DisplayName : null;

            // The site's row for the zone, by any of the place's short names,
            // else by the name itself — freportw and freeportwest are one
            // "West Freeport" and the site files it under one of them.
            var site = maps.Select(index.Zone).FirstOrDefault(z => z is not null);
            if (site is null && tableName is not null)
            {
                var key = ZoneTable.Normalize(tableName);
                site = index.Zones.FirstOrDefault(z => ZoneTable.Normalize(z.LongName) == key);
            }

            places.Add(new NpcPlace(
                zoneId,
                tableName ?? site?.LongName,
                site?.ShortName ?? (maps.Length > 0 ? maps[0] : null),
                maps,
                entries.Count > 0 ? entries[0].Era : null,
                group.Select(v => v.Level).Where(l => l is not null).Select(l => l!.Value).Distinct().Order().ToArray(),
                group.Select(v => v.Id).ToArray()));
        }

        // Named places alphabetical, the unplaceable last: a reader scans for
        // a zone they know, and "somewhere" is not one.
        return places
            .OrderBy(p => p.Name is null ? 1 : 0)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
