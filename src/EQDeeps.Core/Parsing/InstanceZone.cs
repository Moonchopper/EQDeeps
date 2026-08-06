using System.Text.RegularExpressions;

namespace EQDeeps.Core.Parsing;

/// <summary>
/// A zone name split into the place and the instance difficulty it was entered
/// at: "The Estate of Unrest 4 (Refined)" is Unrest at difficulty 4.
///
/// <para>This matters because an instance's difficulty rescales the mobs in it.
/// The same froglok is a different fight at tier 1 and tier 4, so anything that
/// aggregates per mob — mob health above all (F25) — has to key on the pair,
/// not on the name. Difficulty is the only one of the three instance settings
/// the client writes down; see the class doc on
/// <see cref="Mobs.MobHealthIndex"/> for what is missing and why it turns out
/// not to matter much.</para>
///
/// <para>Open world and difficulty 0 are the same bucket, and not by choice: a
/// tier-0 instance prints the bare zone name, exactly as the open world does,
/// so the log cannot tell them apart. It costs nothing here because the two are
/// the same content — d0 is the open world's numbers, which is what "0" means.
/// <see cref="Difficulty"/> is therefore null rather than 0 for both: "no
/// instance suffix was present" is what was observed, and claiming 0 would be
/// inventing a reading of a line that never appeared.</para>
/// </summary>
/// <param name="BaseName">The place, with any instance suffix removed.</param>
/// <param name="Difficulty">The tier number, or null in the open world.</param>
/// <param name="TierName">
/// The server's word for the tier ("Awakened", "Fused"). Carried through rather
/// than mapped to a table, so a tier the server adds or renames shows up as
/// itself instead of disappearing — the same rule stances follow (F23).
/// </param>
public readonly record struct InstanceZone(string BaseName, int? Difficulty, string? TierName)
{
    /// <summary>
    /// Trailing " &lt;n&gt; (&lt;Word&gt;)". The number is capped at two digits and the
    /// tier word at letters so that a zone legitimately ending in a
    /// parenthetical is not mistaken for an instance.
    /// </summary>
    private static readonly Regex Suffix = new(
        @"^(?<base>.+?) (?<n>\d{1,2}) \((?<tier>[A-Za-z][A-Za-z ]*)\)$", RegexOptions.Compiled);

    public static InstanceZone Parse(string zoneName)
    {
        if (string.IsNullOrEmpty(zoneName))
        {
            return new InstanceZone(zoneName ?? string.Empty, null, null);
        }

        var match = Suffix.Match(zoneName);
        return match.Success
            ? new InstanceZone(
                match.Groups["base"].Value,
                int.Parse(match.Groups["n"].Value),
                match.Groups["tier"].Value)
            : new InstanceZone(zoneName, null, null);
    }

    /// <summary>
    /// How to say this zone in one line — the logged form, rebuilt. Display
    /// goes through here rather than through the raw logged string so a name
    /// assembled from stored parts reads identically to one straight off a log
    /// line.
    /// </summary>
    public string Display => Difficulty is { } n && TierName is { Length: > 0 } tier
        ? $"{BaseName} {n} ({tier})"
        : BaseName;
}
