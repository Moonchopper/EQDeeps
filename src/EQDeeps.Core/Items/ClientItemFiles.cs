namespace EQDeeps.Core.Items;

/// <summary>One row of the client's loot-filter file.</summary>
public sealed record LootFilterRow(int ItemId, int FilterId, int IconId, string Name);

/// <summary>
/// The client's loot-filter file, <c>userdata\LF_&lt;Char&gt;_&lt;server&gt;.ini</c>:
/// the one file on disk that numbers items, written by the client as the
/// player sets loot filters and growing for as long as they play (see
/// <c>docs/domain/eq-client-files.md</c>). Format is a comment header then
/// <c>ITEM_ID^FILTER_ID^ICON_ID^ITEM_NAME</c> per line; the ids are the
/// game's own, which is what makes them worth reading. Pure: text in, rows
/// out; a malformed line is skipped, never thrown on.
/// </summary>
public static class LootFilterFile
{
    /// <summary>Where the file lives under an install, for a character on a server; the server is lower-case in the file name as it is in the log's.</summary>
    public static string PathFor(string installRoot, string character, string server) =>
        Path.Combine(installRoot, "userdata", $"LF_{character}_{server}.ini");

    public static IReadOnlyList<LootFilterRow> Parse(string text)
    {
        var rows = new List<LootFilterRow>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split('^');
            if (parts.Length < 4 ||
                !int.TryParse(parts[0], out var id) ||
                !int.TryParse(parts[1], out var filter) ||
                !int.TryParse(parts[2], out var icon))
            {
                continue;
            }

            var name = parts[3].Trim();
            if (name.Length == 0 || id <= 0)
            {
                continue;
            }

            rows.Add(new LootFilterRow(id, filter, icon, name));
        }

        return rows;
    }
}

/// <summary>One row of an inventory dump.</summary>
public sealed record InventoryRow(string Location, string Name, int ItemId, int Count, int Slots);

/// <summary>
/// An <c>/outputfile inventory</c> dump, <c>&lt;Char&gt;_&lt;server&gt;-Inventory.txt</c>
/// beside the client: tab-separated <c>Location Name ID Count Slots</c> with a
/// header row, one line per slot including empty ones (<c>Empty 0 0 0</c>).
/// Only what the character has on them and in the bank, and only as of when
/// they last typed the command — but every row carries the game's item id.
/// </summary>
public static class InventoryDump
{
    public static string PathFor(string installRoot, string character, string server) =>
        Path.Combine(installRoot, $"{character}_{server}-Inventory.txt");

    public static IReadOnlyList<InventoryRow> Parse(string text)
    {
        var rows = new List<InventoryRow>();
        var first = true;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (first)
            {
                first = false;
                if (line.StartsWith("Location\t", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var parts = line.Split('\t');
            if (parts.Length < 5 ||
                !int.TryParse(parts[2], out var id) ||
                !int.TryParse(parts[3], out var count) ||
                !int.TryParse(parts[4], out var slots))
            {
                continue;
            }

            var name = parts[1].Trim();
            if (id <= 0 || name.Length == 0 || string.Equals(name, "Empty", StringComparison.Ordinal))
            {
                continue;
            }

            rows.Add(new InventoryRow(parts[0].Trim(), name, id, count, slots));
        }

        return rows;
    }
}
