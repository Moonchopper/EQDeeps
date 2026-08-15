using System.Collections.Frozen;

namespace EQDeeps.Core.Maps;

/// <summary>One EverQuest expansion, as the era filter names it.</summary>
/// <param name="Id">The code the zone table and the API use: <c>classic</c>, <c>kunark</c>, <c>pop</c>…</param>
/// <param name="Name">The expansion's full title.</param>
/// <param name="Short">What players call it — the form the selector shows.</param>
/// <param name="Year">Release year, to orient anyone who does not have the order by heart.</param>
public sealed record ZoneEra(string Id, string Name, string Short, int Year);

/// <summary>How a row of the zone table got its era, and therefore how much to trust it.</summary>
public enum ZoneEraSource
{
    /// <summary>
    /// From the band the zone's client id falls in (<c>scripts/derive-zone-eras.mjs</c>,
    /// map format doc §5.3). Mechanical, and right for every zone that was
    /// shipped in an expansion's block — which is most of them.
    /// </summary>
    Id,

    /// <summary>
    /// Set by hand, because the band was known to be wrong or knowably tighter:
    /// a launch zone filed in the Kunark block, a 2016 zone in a reused
    /// classic-era gap. Every override carries its reason in the script.
    /// </summary>
    Curated,
}

/// <summary>
/// The expansions in release order, and the one comparison the era filter needs.
///
/// <para><b>Why the player chooses this and the app never guesses.</b> A stock
/// install ships every expansion's maps whether or not the server has unlocked
/// them, and nothing available says which it has: the log names zones the
/// character has already reached, which is a lower bound at best; the map files
/// carry geometry and labels; the client's zone table lists every zone that ever
/// existed. So an era is a setting, chosen once and remembered (issue #57), and
/// with none chosen the World view behaves as if this file did not exist.</para>
///
/// <para><b>What a zone's era means.</b> The <em>earliest</em> expansion the
/// place can exist in — a lower bound. A zone is hidden when its era is later
/// than the chosen one, and a zone whose era is unknown is shown regardless: the
/// same bias as the rest of F27, where a smaller truthful graph beats one that
/// hides something the player can walk into.</para>
/// </summary>
public static class ZoneEras
{
    public static IReadOnlyList<ZoneEra> All { get; } = new ZoneEra[]
    {
        new("classic", "EverQuest", "Classic", 1999),
        new("kunark", "The Ruins of Kunark", "Kunark", 2000),
        new("velious", "The Scars of Velious", "Velious", 2000),
        new("luclin", "The Shadows of Luclin", "Luclin", 2001),
        new("pop", "The Planes of Power", "PoP", 2002),
        new("loy", "The Legacy of Ykesha", "LoY", 2003),
        new("ldon", "Lost Dungeons of Norrath", "LDoN", 2003),
        new("god", "Gates of Discord", "GoD", 2004),
        new("oow", "Omens of War", "OoW", 2004),
        new("don", "Dragons of Norrath", "DoN", 2005),
        new("dodh", "Depths of Darkhollow", "DoDH", 2005),
        new("por", "Prophecy of Ro", "PoR", 2006),
        new("tss", "The Serpent's Spine", "TSS", 2006),
        new("tbs", "The Buried Sea", "TBS", 2007),
        new("sof", "Secrets of Faydwer", "SoF", 2007),
        new("sod", "Seeds of Destruction", "SoD", 2008),
        new("uf", "Underfoot", "UF", 2009),
        new("hot", "House of Thule", "HoT", 2010),
        new("voa", "Veil of Alaris", "VoA", 2011),
        new("rof", "Rain of Fear", "RoF", 2012),
        new("cotf", "Call of the Forsaken", "CotF", 2013),
        new("tds", "The Darkened Sea", "TDS", 2014),
        new("tbm", "The Broken Mirror", "TBM", 2015),
        new("eok", "Empires of Kunark", "EoK", 2016),
        new("ros", "Ring of Scale", "RoS", 2017),
        new("tbl", "The Burning Lands", "TBL", 2018),
        new("tov", "Torment of Velious", "ToV", 2019),
        new("cov", "Claws of Veeshan", "CoV", 2020),
        new("tol", "Terror of Luclin", "ToL", 2021),
        new("nos", "Night of Shadows", "NoS", 2022),
        new("ls", "Laurion's Song", "LS", 2023),
        new("tob", "The Outer Brood", "ToB", 2024),
    };

    private static readonly FrozenDictionary<string, int> Ordinals = All
        .Select((era, index) => (era.Id, index))
        .ToFrozenDictionary(x => x.Id, x => x.index, StringComparer.OrdinalIgnoreCase);

    public static ZoneEra? Find(string? id) =>
        id is not null && Ordinals.TryGetValue(id, out var index) ? All[index] : null;

    public static bool IsKnown(string? id) => id is not null && Ordinals.ContainsKey(id);

    /// <summary>
    /// Whether a zone of era <paramref name="zoneEra"/> exists on a server that
    /// has unlocked everything through <paramref name="through"/>.
    ///
    /// <para>Unknown on either side means yes. A zone the table could not
    /// place is shown rather than hidden, and no chosen era — or one this build
    /// does not recognise — is no filter at all, so the World view is exactly
    /// what it was before eras existed.</para>
    /// </summary>
    public static bool Within(string? zoneEra, string? through)
    {
        if (zoneEra is null || through is null
            || !Ordinals.TryGetValue(zoneEra, out var zone)
            || !Ordinals.TryGetValue(through, out var limit))
        {
            return true;
        }

        return zone <= limit;
    }
}
