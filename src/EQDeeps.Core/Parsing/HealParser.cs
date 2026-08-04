using EQDeeps.Core.Events;

namespace EQDeeps.Core.Parsing;

/// <summary>
/// Heal grammars, anchored on " healed " / " been healed" because a flavor
/// sentence can precede the heal on the same line ("Your ward heals you as it
/// breaks! You healed ..."). Overheal notation "for &lt;landed&gt; (&lt;potential&gt;) hit
/// points" carries the full roll in parentheses.
///
/// Two server families write heals differently and both are handled here:
/// live's "<c>… for N hit points by &lt;Spell&gt;.</c>" and EMU's
/// "<c>&lt;healer&gt; has healed &lt;target&gt; for N points of damage. (&lt;Spell&gt;)</c>"
/// — same event, different wording for the unit, the verb, and where the spell
/// goes. EMU also annotates pets inline, exactly as it does on damage lines.
/// </summary>
public static class HealParser
{
    public static HealEvent? Parse(string action, ParserOptions options)
    {
        // Trailing "(...)": a known modifier becomes flags; anything else is
        // the spell, which is how EMU servers name a heal. Same split the
        // damage grammar makes, for the same reason.
        var body = action;
        var modifiers = HitModifiers.None;
        string? trailingSpell = null;
        if (body.Length > 0 && body[^1] == ')')
        {
            var open = body.LastIndexOf(" (", StringComparison.Ordinal);
            if (open > 0)
            {
                var inner = body[(open + 2)..^1];
                if (ModifierParser.TryParse(inner, out modifiers))
                {
                    body = body[..open];
                }
                else if (!inner.StartsWith("Owner: ", StringComparison.Ordinal))
                {
                    trailingSpell = inner;
                    modifiers = HitModifiers.None;
                    body = body[..open];
                }
            }
        }

        // EMU pet-owner annotation: "<pet> (Owner: <player>) has healed …".
        // The text in front of it is the pet's full name, which is worth more
        // than the last-word guess below — pet names are several words long.
        string? ownerName = null;
        string? ownerSubject = null;
        var ownerAt = body.IndexOf(" (Owner: ", StringComparison.Ordinal);
        if (ownerAt > 0)
        {
            var close = body.IndexOf(')', ownerAt);
            if (close > 0)
            {
                ownerSubject = body[..ownerAt];
                ownerName = body[(ownerAt + " (Owner: ".Length)..close];
                body = ownerSubject + body[(close + 1)..];
            }
        }

        HealEvent? heal = null;
        const string Been = " been healed";
        var beenAt = body.IndexOf(Been, StringComparison.Ordinal);
        if (beenAt > 0)
        {
            heal = ParseReceived(body, beenAt, modifiers, options);
        }
        else
        {
            const string Healed = " healed ";
            var healedAt = body.IndexOf(Healed, StringComparison.Ordinal);
            if (healedAt > 0)
            {
                heal = ParseGiven(body, healedAt, ownerSubject, modifiers, options);
            }
        }

        if (heal is null)
        {
            return null;
        }

        if (trailingSpell is not null && heal.Spell is null)
        {
            heal = heal with { Spell = trailingSpell };
        }

        return ownerName is not null ? heal with { HealerOwner = ownerName } : heal;
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

    private static HealEvent? ParseGiven(
        string body, int healedAt, string? ownerSubject, HitModifiers modifiers, ParserOptions options)
    {
        // "<healer> has|have healed <target> …" — EMU's auxiliary verb. Without
        // stripping it the last-word rule below would name the healer "has".
        var healerPart = body[..healedAt];
        if (healerPart.EndsWith(" has", StringComparison.Ordinal))
        {
            healerPart = healerPart[..^4];
        }
        else if (healerPart.EndsWith(" have", StringComparison.Ordinal))
        {
            healerPart = healerPart[..^5];
        }

        // An owner annotation already delimited the healer exactly, so trust it
        // over the heuristic.
        if (ownerSubject is not null && healerPart == ownerSubject)
        {
            return ParseAmountClause(
                body, healedAt + " healed ".Length - 1,
                Names.Resolve(ownerSubject, options), target: null, modifiers, options);
        }

        // Healer = last word before " healed " (a possible flavor sentence sits in
        // front with no reliable delimiter). Pets are the one multi-word healer we
        // recognize: "<owner>`s pet healed ...".
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

        // Live says "hit points"; EMU says "points of damage" — for a heal,
        // which reads oddly but is the same number.
        if (rest.StartsWith(" hit points", StringComparison.Ordinal))
        {
            rest = rest[" hit points".Length..];
        }
        else if (rest.StartsWith(" points of damage", StringComparison.Ordinal))
        {
            rest = rest[" points of damage".Length..];
        }
        else
        {
            return null;
        }

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
