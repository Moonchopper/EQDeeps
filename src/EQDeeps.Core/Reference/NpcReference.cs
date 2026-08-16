using System.Text.Json;

namespace EQDeeps.Core.Reference;

/// <summary>One NPC in the reference index: a name, the level it is listed at, and the site's id.</summary>
public sealed record NpcIndexEntry(string Name, int? Level, int Id);

/// <summary>A line of a listed loot table: the item, how often it drops, and its icon.</summary>
public sealed record NpcLootLine(int ItemId, string Item, double DropPercent, int IconId, string? Damage);

/// <summary>Where a listed NPC spawns: the zone, and the points it is placed at.</summary>
public sealed record NpcSpawnZone(string ShortName, string LongName, int SpawnPoints, IReadOnlyList<double[]> Locations);

/// <summary>
/// Everything a reference site lists about one NPC. Every field is optional
/// on purpose: this is someone else's data, read from a site that says of
/// itself that it is in early alpha, and a missing number must read as
/// "not listed" rather than as a zero.
/// </summary>
public sealed record NpcDetail(
    int Id,
    string Name,
    int? Level,
    int? MaxLevel,
    int? Hp,
    int? Ac,
    string? Race,
    string? Class,
    string? Faction,
    int? RespawnSeconds,
    int? MinDamage,
    int? MaxDamage,
    IReadOnlyList<string> Specials,
    IReadOnlyList<NpcLootLine> Loot,
    IReadOnlyList<NpcSpawnZone> Zones);

/// <summary>
/// Reads the shapes EQLBase publishes (ADR-020). Two files:
///
/// <list type="bullet">
/// <item><c>/data/search-index.json</c> — one array of <c>[name, type, id]</c>
/// for everything the site knows; type <c>"n"</c> is an NPC and its name
/// carries the level in parentheses, "Fippy Darkpaw (5)".</item>
/// <item><c>/data/npcs/&lt;id/1000&gt;.json</c> — an object keyed by id, sharded a
/// thousand ids at a time, holding the stat block and the loot table.</item>
/// </list>
///
/// <para>Pure: text in, records out, so the whole format lives under test
/// without a network. Tolerant by policy — a row that does not parse is
/// skipped, a field that is missing stays null, and a shape that changes
/// entirely yields an empty list rather than an exception. Their site is
/// alpha and this app must not break when it moves.</para>
/// </summary>
public static class NpcReferenceFormat
{
    /// <summary>Which shard file holds an id.</summary>
    public static int ShardOf(int id) => id / 1000;

    /// <summary>The path a shard lives at, relative to the site root.</summary>
    public static string ShardPath(int id) => $"/data/npcs/{ShardOf(id)}.json";

    /// <summary>The path of the whole-site name index.</summary>
    public const string IndexPath = "/data/search-index.json";

    /// <summary>The NPC rows of the index, in the order the site lists them.</summary>
    public static IReadOnlyList<NpcIndexEntry> ParseIndex(string json)
    {
        var entries = new List<NpcIndexEntry>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return entries;
            }

            foreach (var row in doc.RootElement.EnumerateArray())
            {
                // [name, type, id] — anything else is a shape we do not know.
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 3)
                {
                    continue;
                }

                var name = row[0].ValueKind == JsonValueKind.String ? row[0].GetString() : null;
                var type = row[1].ValueKind == JsonValueKind.String ? row[1].GetString() : null;
                if (name is null || type != "n" || !TryInt(row[2], out var id))
                {
                    continue;
                }

                var (bare, level) = SplitLevel(name);
                if (bare.Length > 0)
                {
                    entries.Add(new NpcIndexEntry(bare, level, id));
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return entries;
    }

    /// <summary>
    /// Splits "Fippy Darkpaw (5)" into its name and level. A trailing
    /// parenthesis that is not a number is part of the name — "a gnoll
    /// (guard)" would be — so only digits are taken.
    /// </summary>
    public static (string Name, int? Level) SplitLevel(string listed)
    {
        var s = listed.Trim();
        if (s.Length < 4 || s[^1] != ')')
        {
            return (s, null);
        }

        var open = s.LastIndexOf('(');
        if (open <= 0)
        {
            return (s, null);
        }

        var inner = s.AsSpan(open + 1, s.Length - open - 2);
        return int.TryParse(inner, out var level) && level > 0
            ? (s[..open].TrimEnd(), level)
            : (s, null);
    }

    /// <summary>The NPCs in one shard file, by id.</summary>
    public static IReadOnlyDictionary<int, NpcDetail> ParseShard(string json)
    {
        var byId = new Dictionary<int, NpcDetail>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return byId;
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var detail = ReadDetail(property.Value);
                if (detail is not null)
                {
                    byId[detail.Id] = detail;
                }
            }
        }
        catch (JsonException)
        {
            return new Dictionary<int, NpcDetail>();
        }

        return byId;
    }

    private static NpcDetail? ReadDetail(JsonElement e)
    {
        var id = Int(e, "id");
        var name = Str(e, "name");
        if (id is null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new NpcDetail(
            id.Value,
            name!,
            Int(e, "level"),
            Int(e, "maxLevel"),
            Int(e, "hp"),
            Int(e, "ac"),
            Str(e, "race"),
            Str(e, "className"),
            Str(e, "faction"),
            Int(e, "respawn"),
            Int(e, "minDmg"),
            Int(e, "maxDmg"),
            Strings(e, "specials"),
            Loot(e),
            Zones(e));
    }

    private static IReadOnlyList<NpcLootLine> Loot(JsonElement e)
    {
        if (!e.TryGetProperty("loot", out var loot) || loot.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var lines = new List<NpcLootLine>();
        foreach (var row in loot.EnumerateArray())
        {
            // [itemId, name, dropPercent, iconId, "min/delay"]
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 3)
            {
                continue;
            }

            if (!TryInt(row[0], out var itemId) || row[1].ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var percent = row[2].ValueKind == JsonValueKind.Number ? row[2].GetDouble() : 0;
            var icon = row.GetArrayLength() > 3 && TryInt(row[3], out var i) ? i : 0;
            var damage = row.GetArrayLength() > 4 && row[4].ValueKind == JsonValueKind.String
                ? row[4].GetString()
                : null;
            lines.Add(new NpcLootLine(itemId, row[1].GetString()!, percent, icon,
                string.IsNullOrWhiteSpace(damage) ? null : damage));
        }

        return lines;
    }

    private static IReadOnlyList<NpcSpawnZone> Zones(JsonElement e)
    {
        if (!e.TryGetProperty("zones", out var zones) || zones.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<NpcSpawnZone>();
        foreach (var z in zones.EnumerateArray())
        {
            if (z.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var shortName = Str(z, "zone") ?? "";
            var longName = Str(z, "longName") ?? shortName;
            if (shortName.Length == 0 && longName.Length == 0)
            {
                continue;
            }

            var locs = new List<double[]>();
            if (z.TryGetProperty("locs", out var l) && l.ValueKind == JsonValueKind.Array)
            {
                foreach (var point in l.EnumerateArray())
                {
                    if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2)
                    {
                        continue;
                    }

                    var coords = new List<double>();
                    foreach (var c in point.EnumerateArray())
                    {
                        coords.Add(c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0);
                    }

                    locs.Add(coords.ToArray());
                }
            }

            list.Add(new NpcSpawnZone(shortName, longName, Int(z, "spawnPoints") ?? locs.Count, locs));
        }

        return list;
    }

    private static IReadOnlyList<string> Strings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string>();
        foreach (var item in a.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
            {
                list.Add(s);
            }
        }

        return list;
    }

    /// <summary>
    /// A number, or nothing. <c>TryGetInt32</c> throws when the element is not
    /// a number at all — it only reports whether a number fits — so the kind
    /// is checked first. Hostile input is the rule here, not the exception.
    /// </summary>
    private static bool TryInt(JsonElement e, out int value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;
}
