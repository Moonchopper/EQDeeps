using System.Text.RegularExpressions;

namespace EQDeeps.Core.Parsing;

/// <summary>
/// A zone name split into the place and the instance settings it was entered
/// with: "The Estate of Unrest 4 (Refined)" is Unrest at difficulty 4, and
/// "The Plane of Fear - Group 3 (Fused)" is the Plane of Fear, mode Group, at
/// difficulty 3.
///
/// <para>This matters because both settings rescale the mobs in it. The same
/// froglok is a different fight at tier 1 and tier 4, and — the owner
/// confirmed, 2026-09-20 — a different fight again solo versus group, so
/// anything that aggregates per mob — mob health above all (F25) — has to key
/// on all of it, not on the name. Difficulty and the mode marker are the only
/// two of the three instance settings the client writes down (respawn timing
/// is never logged); see the class doc on <see cref="Mobs.MobHealthIndex"/>
/// for what is missing and why it turns out not to matter much.</para>
///
/// <para>Open world and difficulty 0 are the same bucket, and not by choice: a
/// tier-0 instance prints the bare zone name, exactly as the open world does,
/// so the log cannot tell them apart. It costs nothing here because the two are
/// the same content — d0 is the open world's numbers, which is what "0" means.
/// <see cref="Difficulty"/> is therefore null rather than 0 for both: "no
/// instance suffix was present" is what was observed, and claiming 0 would be
/// inventing a reading of a line that never appeared.</para>
/// </summary>
/// <param name="BaseName">The place alone, with the mode marker and any tier suffix removed.</param>
/// <param name="Difficulty">The tier number, or null in the open world.</param>
/// <param name="TierName">
/// The server's word for the tier ("Awakened", "Fused"). Carried through rather
/// than mapped to a table, so a tier the server adds or renames shows up as
/// itself instead of disappearing — the same rule stances follow (F23).
/// </param>
/// <param name="Mode">
/// "Solo" or "Group" when the zone line carried the marker (see
/// <see cref="ModeMarker"/>), else null. Unlike <see cref="TierName"/> this is
/// a <b>closed set, on purpose</b>: the marker has only ever been observed on
/// five zones printing exactly these two words (§3.9b of the log-format doc),
/// and there is no evidence yet of a third. A mode the server invents later
/// reads as part of the place name until someone adds it here — the same safe
/// failure this line already has for every other unrecognized shape, and
/// exactly today's behaviour for every mode. Last positional parameter,
/// defaulting to null, so every existing three-argument construction keeps
/// compiling unchanged.
/// </param>
public readonly record struct InstanceZone(
    string BaseName, int? Difficulty, string? TierName, string? Mode = null)
{
    /// <summary>
    /// Trailing " &lt;n&gt; (&lt;Word&gt;)". The number is capped at two digits and the
    /// tier word at letters so that a zone legitimately ending in a
    /// parenthetical is not mistaken for an instance.
    /// </summary>
    private static readonly Regex Suffix = new(
        @"^(?<base>.+?) (?<n>\d{1,2}) \((?<tier>[A-Za-z][A-Za-z ]*)\)$", RegexOptions.Compiled);

    /// <summary>
    /// Trailing " - Solo" or " - Group", matched case-sensitively exactly as
    /// logged (the client has never been observed to vary the casing, and
    /// there is no reason to guess at a normalization it has not asked for).
    /// A closed set of two literal words rather than a general "split on the
    /// last ' - '" rule, because real zone names contain " - " themselves —
    /// "Neriak - Foreign Quarter", "Neriak - Commons", "Neriak - Third Gate",
    /// "Neriak - Fourth Gate" — and a general rule would cut all four in half.
    /// </summary>
    private static readonly Regex ModeMarker = new(
        @"^(?<base>.+?) - (?<mode>Solo|Group)$", RegexOptions.Compiled);

    public static InstanceZone Parse(string zoneName)
    {
        if (string.IsNullOrEmpty(zoneName))
        {
            return new InstanceZone(zoneName ?? string.Empty, null, null);
        }

        // The tier comes off first: its regex anchors on the end of the
        // string, and the mode marker sits before the tier, not after it.
        // Two sequential matches — tier, then mode on whatever base the tier
        // match left — keeps each regex simple and leaves the tier's own
        // properties (the two-digit cap, "not mistaken for an instance")
        // exactly as they were before this marker was known about.
        var tier = Suffix.Match(zoneName);
        string baseAfterTier;
        int? difficulty;
        string? tierName;
        if (tier.Success)
        {
            baseAfterTier = tier.Groups["base"].Value;
            difficulty = int.Parse(tier.Groups["n"].Value);
            tierName = tier.Groups["tier"].Value;
        }
        else
        {
            baseAfterTier = zoneName;
            difficulty = null;
            tierName = null;
        }

        var mode = ModeMarker.Match(baseAfterTier);
        return mode.Success
            ? new InstanceZone(mode.Groups["base"].Value, difficulty, tierName, mode.Groups["mode"].Value)
            : new InstanceZone(baseAfterTier, difficulty, tierName);
    }

    /// <summary>
    /// The place plus the mode marker, exactly what <see cref="BaseName"/>
    /// returned before this type learned to read the marker — the no-migration
    /// key. F25/F26 and the fight-list join key on this, not on the new
    /// <see cref="BaseName"/>: the owner confirmed the mode rescales the
    /// instance the way a tier does, so a solo-scaled boss and a group-scaled
    /// one must stay separate measurements, and every key already on disk
    /// under <c>%AppData%\EQDeeps\mobs\</c> and <c>attacks\</c> was computed
    /// from the old <c>BaseName</c> — which is exactly this string for every
    /// zone that was ever marked, since the mode was part of the place name as
    /// far as those stores knew. Nothing is stranded, nothing is
    /// double-counted, no migration runs.
    /// </summary>
    public string KeyName => Mode is { Length: > 0 } mode ? $"{BaseName} - {mode}" : BaseName;

    /// <summary>
    /// How to say this zone in one line — the logged form, rebuilt. Display
    /// goes through here rather than through the raw logged string so a name
    /// assembled from stored parts reads identically to one straight off a log
    /// line.
    /// </summary>
    public string Display
    {
        get
        {
            var withMode = KeyName;
            return Difficulty is { } n && TierName is { Length: > 0 } tier
                ? $"{withMode} {n} ({tier})"
                : withMode;
        }
    }
}
