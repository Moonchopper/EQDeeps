using EQDeeps.Core.Events;

namespace EQDeeps.Core.Parsing;

/// <summary>
/// Parses the parenthesized modifier suffix on damage/heal lines, e.g.
/// "(Lucky Critical Twincast)" → flags. Semantics preserved from the incumbent:
/// Crippling Blow and Deadly Strike are old-style crit spellings, and a Riposte
/// token accompanied by Strikethrough means the attacker struck through the
/// defender's riposte — strikethrough wins and the riposte flag is dropped.
/// </summary>
public static class ModifierParser
{
    // Multi-word phrases must precede their prefixes so the longest match wins.
    private static readonly (string Phrase, HitModifiers Flag)[] Phrases =
    [
        ("Double Bow Shot", HitModifiers.DoubleBowShot),
        ("Finishing Blow", HitModifiers.FinishingBlow),
        ("Slay Undead", HitModifiers.SlayUndead),
        ("Wild Rampage", HitModifiers.WildRampage),
        ("Crippling Blow", HitModifiers.Critical),
        ("Deadly Strike", HitModifiers.Critical),
        ("Critical", HitModifiers.Critical),
        ("Lucky", HitModifiers.Lucky),
        ("Twincast", HitModifiers.Twincast),
        ("Flurry", HitModifiers.Flurry),
        ("Riposte", HitModifiers.Riposte),
        ("Strikethrough", HitModifiers.Strikethrough),
        ("Rampage", HitModifiers.Rampage),
        ("Assassinate", HitModifiers.Assassinate),
        ("Headshot", HitModifiers.Headshot),
        ("Locked", HitModifiers.Locked),
    ];

    /// <summary>Parses modifiers, ignoring any unrecognized tokens.</summary>
    public static HitModifiers Parse(string? text)
    {
        TryParse(text, out var modifiers);
        return modifiers;
    }

    /// <summary>
    /// Parses modifiers; returns false if any token was not a known modifier
    /// (callers use that to tell EMU spell-name suffixes apart from modifiers).
    /// </summary>
    public static bool TryParse(string? text, out HitModifiers modifiers)
    {
        modifiers = HitModifiers.None;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        var allKnown = true;
        var pos = 0;
        while (pos < text.Length)
        {
            if (text[pos] == ' ')
            {
                pos++;
                continue;
            }

            var matched = false;
            foreach (var (phrase, flag) in Phrases)
            {
                if (MatchesAt(text, pos, phrase))
                {
                    modifiers |= flag;
                    pos += phrase.Length;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                allKnown = false;
                var nextSpace = text.IndexOf(' ', pos);
                pos = nextSpace < 0 ? text.Length : nextSpace + 1;
            }
        }

        if ((modifiers & HitModifiers.Riposte) != 0 && (modifiers & HitModifiers.Strikethrough) != 0)
        {
            modifiers &= ~HitModifiers.Riposte;
        }

        return allKnown;
    }

    private static bool MatchesAt(string text, int pos, string phrase)
    {
        if (!text.AsSpan(pos).StartsWith(phrase, StringComparison.Ordinal))
        {
            return false;
        }

        var end = pos + phrase.Length;
        return end == text.Length || text[end] == ' ';
    }
}
