using EQDeeps.Core.Events;

namespace EQDeeps.Core.Parsing;

/// <summary>
/// Heal grammars, anchored on " healed " / " been healed" because a flavor
/// sentence can precede the heal on the same line ("Your ward heals you as it
/// breaks! You healed ..."). Overheal notation "for &lt;landed&gt; (&lt;potential&gt;) hit
/// points" carries the full roll in parentheses.
/// </summary>
public static class HealParser
{
    public static HealEvent? Parse(string action, ParserOptions options)
    {
        // Strip a trailing modifier suffix first: "... (Lucky Critical)".
        var body = action;
        var modifiers = HitModifiers.None;
        if (body.Length > 0 && body[^1] == ')')
        {
            var open = body.LastIndexOf(" (", StringComparison.Ordinal);
            if (open > 0 && ModifierParser.TryParse(body[(open + 2)..^1], out modifiers))
            {
                body = body[..open];
            }
        }

        const string Been = " been healed";
        var beenAt = body.IndexOf(Been, StringComparison.Ordinal);
        if (beenAt > 0)
        {
            return ParseReceived(body, beenAt, modifiers, options);
        }

        const string Healed = " healed ";
        var healedAt = body.IndexOf(Healed, StringComparison.Ordinal);
        if (healedAt > 0)
        {
            return ParseGiven(body, healedAt, modifiers, options);
        }

        return null;
    }

    private static HealEvent? ParseReceived(string body, int beenAt, HitModifiers modifiers, ParserOptions options)
    {
        // "<target> has|have been healed [over time] for N [(P)] hit points [by <Spell>]."
        var subject = body[..beenAt];
        if (subject.EndsWith(" has", StringComparison.Ordinal))
        {
            subject = subject[..^4];
        }
        else if (subject.EndsWith(" have", StringComparison.Ordinal))
        {
            subject = subject[..^5];
        }
        else
        {
            return null;
        }

        var target = Names.Resolve(subject, options);
        return ParseAmountClause(body, beenAt + " been healed".Length, healer: null, target, modifiers, options);
    }

    private static HealEvent? ParseGiven(string body, int healedAt, HitModifiers modifiers, ParserOptions options)
    {
        // Healer = last word before " healed " (a possible flavor sentence sits in
        // front with no reliable delimiter). Pets are the one multi-word healer we
        // recognize: "<owner>`s pet healed ...".
        var healerPart = body[..healedAt];
        var lastSpace = healerPart.LastIndexOf(' ');
        var healerWord = healerPart[(lastSpace + 1)..];
        var healer = healerWord;
        if (healerWord == "pet" && lastSpace > 0)
        {
            var prevSpace = healerPart.LastIndexOf(' ', lastSpace - 1);
            var owner = healerPart[(prevSpace + 1)..lastSpace];
            if (owner.EndsWith("`s", StringComparison.Ordinal) || owner.EndsWith("'s", StringComparison.Ordinal))
            {
                healer = owner + " pet";
            }
        }

        healer = Names.Resolve(healer, options);
        return ParseAmountClause(body, healedAt + " healed ".Length - 1, healer, target: null, modifiers, options);
    }

    private static HealEvent? ParseAmountClause(
        string body, int from, string? healer, string? target, HitModifiers modifiers, ParserOptions options)
    {
        var overTime = false;
        var rest = body.AsSpan(from);

        if (target is null)
        {
            // "<target>[ over time] for ..." — target text sits between the anchor
            // and the first " for ".
            var forAt = rest.IndexOf(" for ", StringComparison.Ordinal);
            if (forAt <= 0)
            {
                return null;
            }

            var targetText = rest[..forAt].Trim();
            if (targetText.EndsWith(" over time", StringComparison.Ordinal))
            {
                overTime = true;
                targetText = targetText[..^" over time".Length];
            }

            var targetName = targetText.ToString();
            target = Names.IsSelfPronoun(targetName)
                ? healer ?? targetName
                : Names.Resolve(targetName, options);
            rest = rest[(forAt + " for ".Length)..];
        }
        else
        {
            if (rest.StartsWith(" over time", StringComparison.Ordinal))
            {
                overTime = true;
                rest = rest[" over time".Length..];
            }

            if (!rest.StartsWith(" for ", StringComparison.Ordinal))
            {
                return null;
            }

            rest = rest[" for ".Length..];
        }

        // "N [(P)] hit points [by <Spell>][.]"
        if (!TryReadUInt(ref rest, out var landed))
        {
            return null;
        }

        var potential = landed;
        if (rest.StartsWith(" (", StringComparison.Ordinal))
        {
            var close = rest.IndexOf(')');
            var inner = rest[2..(close < 0 ? rest.Length : close)];
            if (close < 0 || !TryParseAll(inner, out potential))
            {
                return null;
            }

            rest = rest[(close + 1)..];
        }

        if (!rest.StartsWith(" hit points", StringComparison.Ordinal))
        {
            return null;
        }

        rest = rest[" hit points".Length..];

        string? spell = null;
        if (rest.StartsWith(" by ", StringComparison.Ordinal))
        {
            var text = rest[" by ".Length..].TrimEnd();
            if (text.Length > 0 && text[^1] == '.')
            {
                text = text[..^1];
            }

            spell = text.ToString();
        }

        return new HealEvent(healer, target, landed, potential, overTime, spell, modifiers);
    }

    private static bool TryReadUInt(ref ReadOnlySpan<char> text, out uint value)
    {
        value = 0;
        ulong acc = 0;
        var i = 0;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            acc = acc * 10 + (uint)(text[i] - '0');
            if (acc > uint.MaxValue)
            {
                return false;
            }

            i++;
        }

        if (i == 0)
        {
            return false;
        }

        value = (uint)acc;
        text = text[i..];
        return true;
    }

    private static bool TryParseAll(ReadOnlySpan<char> text, out uint value)
    {
        var copy = text;
        return TryReadUInt(ref copy, out value) && copy.IsEmpty;
    }
}
