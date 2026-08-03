using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EQDeeps.Core.Gear;

/// <summary>
/// Reads the tab-delimited file EverQuest writes for <c>/outputfile inventory</c>.
///
/// <para>Two sections. The first is one row per slot,
/// <c>Location, Name, ID, Count, Slots</c>, where a location may carry
/// <c>-Slot&lt;n&gt;</c> suffixes: one level down from an equipment slot is an
/// augment, one level down from a bag is its contents, two levels down is an
/// augment inside a bagged item. The second, after a blank line, is the
/// <c>KeyRing</c> list of owned equipment and augments.</para>
///
/// <para>Equipment is identified by <em>excluding</em> the containers — bags,
/// banks, and the cursor — rather than by matching a list of slot names. EQ
/// Legends already ships a generic "Any Slot", and a parser that only knows
/// yesterday's slot names silently drops today's gear.</para>
///
/// <para>Pure and total, like the log parsers: ragged rows, an absent second
/// section, or an unrecognisable file yield less data, never an exception.</para>
/// </summary>
public static class InventoryFileParser
{
    private static readonly Regex SubSlotSuffix =
        new(@"-Slot\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Containers and the cursor — everything here is carried, not worn.</summary>
    private static readonly Regex NonEquipmentSlot =
        new(@"^(General\s*\d+|Bank\d+|SharedBank\d+|Held)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The EQ Legends upgrade level, e.g. "Jade Earring +4".</summary>
    private static readonly Regex UpgradeSuffix =
        new(@"^(?<base>.+?)\s\+(?<n>\d{1,3})$", RegexOptions.Compiled);

    private const string EmptySlot = "Empty";

    /// <summary>The name the game writes for a character's dump: &lt;Character&gt;_&lt;server&gt;-Inventory.txt.</summary>
    public static string FileNameFor(string character, string server) =>
        $"{character}_{server}-Inventory.txt";

    /// <summary>
    /// Parses a dump. Returns null when the file yields no equipped items at
    /// all — a truncated or unrelated file should not be recorded as "this
    /// player wore nothing", which is a claim, not an absence of data.
    /// </summary>
    public static GearSnapshot? Parse(
        IEnumerable<string> lines, string character, string server, DateTime capturedAt)
    {
        var equipped = new List<(GearItem Item, List<GearItem> Augments)>();
        var keyRing = new List<KeyRingEntry>();
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Index of the slot augment rows currently attach to; -1 once a bag or
        // a bare slot has been seen, since their children are not gear.
        var parent = -1;
        var inKeyRing = false;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var fields = raw.Split('\t');
            var first = fields[0].Trim();

            // Section headers. The KeyRing header also switches sections; a
            // second Location header (concatenated dumps) simply resets.
            if (first.Equals("KeyRing", StringComparison.OrdinalIgnoreCase))
            {
                inKeyRing = true;
                parent = -1;
                continue;
            }

            if (first.Equals("Location", StringComparison.OrdinalIgnoreCase))
            {
                inKeyRing = false;
                parent = -1;
                continue;
            }

            if (fields.Length < 3)
            {
                continue;
            }

            var name = fields[1].Trim();
            var itemId = ParseInt(fields[2]);

            if (inKeyRing)
            {
                if (!IsEmpty(name))
                {
                    keyRing.Add(new KeyRingEntry(first, name, itemId));
                }

                continue;
            }

            var (slot, depth) = SplitSlot(first);

            if (depth == 0)
            {
                // Count every top-level slot, filled or not, so "the second
                // Wrist" keeps its number in a snapshot where the first is bare.
                occurrences[slot] = occurrences.GetValueOrDefault(slot) + 1;

                if (NonEquipmentSlot.IsMatch(slot) || IsEmpty(name))
                {
                    parent = -1;   // children of a bag, or of a bare slot, are not gear
                    continue;
                }

                equipped.Add((NewItem(slot, occurrences[slot], name, itemId), []));
                parent = equipped.Count - 1;
                continue;
            }

            // An augment: one level under an equipment slot we kept. Deeper rows
            // (an augment inside a bagged item) have no parent here and fall away.
            if (depth == 1 && parent >= 0 && !IsEmpty(name))
            {
                equipped[parent].Augments.Add(NewItem(slot, 0, name, itemId));
            }
        }

        if (equipped.Count == 0)
        {
            return null;
        }

        var withAugments = equipped
            .Select(entry => entry.Item with { Augments = entry.Augments })
            .ToList();

        return new GearSnapshot(
            character, server, capturedAt, withAugments, keyRing, ComputeHash(withAugments));
    }

    /// <summary>
    /// Identity of an equipped set: slot, item, and augments. Deliberately not
    /// the whole file — bank contents and coin move constantly and must not
    /// register as a gear change.
    /// </summary>
    private static string ComputeHash(IReadOnlyList<GearItem> equipped)
    {
        var builder = new StringBuilder();
        foreach (var item in equipped)
        {
            builder.Append(item.SlotKey).Append('=').Append(item.ItemId)
                   .Append(':').Append(item.Name);
            foreach (var augment in item.Augments)
            {
                builder.Append('+').Append(augment.ItemId).Append(':').Append(augment.Name);
            }

            builder.Append('|');
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static GearItem NewItem(string slot, int occurrence, string name, int itemId)
    {
        var match = UpgradeSuffix.Match(name);
        return match.Success
            ? new GearItem(slot, occurrence, name, match.Groups["base"].Value,
                ParseInt(match.Groups["n"].Value), itemId, [])
            : new GearItem(slot, occurrence, name, name, 0, itemId, []);
    }

    /// <summary>Strips "-Slot&lt;n&gt;" suffixes, returning the root slot and how deep the row sat.</summary>
    private static (string Slot, int Depth) SplitSlot(string location)
    {
        var slot = location;
        var depth = 0;
        while (SubSlotSuffix.Match(slot) is { Success: true } match)
        {
            slot = slot[..match.Index];
            depth++;
        }

        return (slot.Trim(), depth);
    }

    private static bool IsEmpty(string name) =>
        name.Length == 0 || name.Equals(EmptySlot, StringComparison.OrdinalIgnoreCase);

    private static int ParseInt(string field) =>
        int.TryParse(field.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
}
