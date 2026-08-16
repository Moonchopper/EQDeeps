namespace EQDeeps.Core.Items;

/// <summary>
/// How an item name is compared. The log, the client's own files and the
/// reference sites all name the same item slightly differently, and the
/// registry has to agree with itself about which strings are one item.
/// </summary>
public static class ItemNames
{
    /// <summary>
    /// The base name: EverQuest Legends decorates an upgraded item with a rank
    /// ("Fine Steel Rapier +2") and an exalted one with a tag ("Guise of the
    /// Deceiver (Exaltation)"); the loot-filter file lists them that way too,
    /// under the base item's id, so the base name is what the id belongs to.
    /// A rank of +0 does not occur, and a name that merely ends in a number
    /// ("Wind Rune Kala") has no plus sign and is left alone.
    /// </summary>
    public static string Strip(string name)
    {
        var s = name.Trim();
        // The tag can follow the rank ("+3 (Exaltation)"); take whichever is
        // outermost, then the other.
        s = StripExalted(s);
        var plus = s.LastIndexOf(" +", StringComparison.Ordinal);
        if (plus > 0 && plus + 2 < s.Length && AllDigits(s.AsSpan(plus + 2)))
        {
            s = s[..plus];
        }

        return StripExalted(s);
    }

    private static string StripExalted(string s)
    {
        const string exalted = " (Exaltation)";
        return s.EndsWith(exalted, StringComparison.OrdinalIgnoreCase) ? s[..^exalted.Length].TrimEnd() : s;
    }

    /// <summary>
    /// The registry's key for a name: base name, case-folded, inner runs of
    /// whitespace collapsed. Ordinal-insensitive because the same item arrives
    /// as "Raw-Hide Mask" from the client and "Raw-hide Mask" from a site,
    /// and neither is wrong.
    /// </summary>
    public static string Key(string name)
    {
        var s = Strip(name);
        Span<char> buffer = stackalloc char[s.Length];
        var n = 0;
        var lastSpace = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (lastSpace || n == 0)
                {
                    continue;
                }

                buffer[n++] = ' ';
                lastSpace = true;
            }
            else
            {
                buffer[n++] = char.ToLowerInvariant(c);
                lastSpace = false;
            }
        }

        if (n > 0 && buffer[n - 1] == ' ')
        {
            n--;
        }

        return new string(buffer[..n]);
    }

    private static bool AllDigits(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return !s.IsEmpty;
    }
}
