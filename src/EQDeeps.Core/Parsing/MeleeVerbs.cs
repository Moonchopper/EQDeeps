namespace EQDeeps.Core.Parsing;

/// <summary>
/// The game's melee attack vocabulary. Log lines use the bare form for first
/// person ("You crush") and the s-form for third person ("crushes"); records
/// store the capitalized s-form ("Crushes") as the skill subtype.
/// </summary>
public static class MeleeVerbs
{
    private static readonly Dictionary<string, string> BareToS = new(StringComparer.Ordinal)
    {
        ["backstab"] = "backstabs",
        ["bash"] = "bashes",
        ["bite"] = "bites",
        ["claw"] = "claws",
        ["cleave"] = "cleaves",
        ["crush"] = "crushes",
        ["frenzy"] = "frenzies",
        ["gore"] = "gores",
        ["hit"] = "hits",
        ["kick"] = "kicks",
        ["learn"] = "learns",
        ["maul"] = "mauls",
        ["pierce"] = "pierces",
        ["punch"] = "punches",
        ["reave"] = "reaves",
        ["rend"] = "rends",
        ["shoot"] = "shoots",
        ["slam"] = "slams",
        ["slash"] = "slashes",
        ["slice"] = "slices",
        ["smash"] = "smashes",
        ["smite"] = "smites",
        ["stab"] = "stabs",
        ["sting"] = "stings",
        ["strike"] = "strikes",
        ["sweep"] = "sweeps",
    };

    private static readonly HashSet<string> SForms = new(BareToS.Values, StringComparer.Ordinal);

    /// <summary>S-form for a bare verb ("crush" → "crushes"), or null if not a melee verb.</summary>
    public static string? FromBare(string word) =>
        BareToS.TryGetValue(word, out var s) ? s : null;

    /// <summary>True if the word is a third-person s-form melee verb ("crushes").</summary>
    public static bool IsSForm(ReadOnlySpan<char> word)
    {
        // Verbs are always lowercase in game output; name words are capitalized,
        // so an ordinal (case-sensitive) match doubles as a name/verb filter.
        foreach (var s in SForms)
        {
            if (word.SequenceEqual(s))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Display subtype for a verb in either form ("crush"/"crushes" → "Crushes").</summary>
    public static string? SubTypeOf(string word)
    {
        var s = BareToS.TryGetValue(word, out var mapped) ? mapped : SForms.Contains(word) ? word : null;
        return s is null ? null : Names.CapitalizeFirst(s);
    }
}
