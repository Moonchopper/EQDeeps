using EQDeeps.Core.Events;

namespace EQDeeps.Core.Parsing;

/// <summary>Taunts, zone transitions, spell resists, membership, /who, and experience lines.</summary>
public static class MiscParser
{
    public static GameEvent? Parse(string action, ParserOptions options)
    {
        return ParseTaunt(action, options)
            ?? ParseZone(action)
            ?? ParseResist(action, options)
            ?? ParseExperience(action)
            ?? ParseFaction(action)
            ?? ParseLoot(action, options)
            ?? ParseConsider(action)
            ?? ParseMembership(action, options)
            ?? ParseWho(action);
    }

    private static GameEvent? ParseConsider(string action)
    {
        // "A bat regards you indifferently -- You could probably win this
        // fight. (Lvl: 7)" — the attitude infix identifies the line; the
        // threat clause varies freely and is dropped; the level suffix is a
        // modern-server addition.
        foreach (var (infix, attitude) in ConsiderPatterns)
        {
            var i = action.IndexOf(infix, StringComparison.Ordinal);
            if (i <= 0)
            {
                continue;
            }

            int? level = null;
            var lvl = action.LastIndexOf(" (Lvl: ", StringComparison.Ordinal);
            if (lvl > 0 && action.EndsWith(")", StringComparison.Ordinal) &&
                int.TryParse(action.AsSpan(lvl + " (Lvl: ".Length, action.Length - lvl - " (Lvl: ".Length - 1),
                    out var parsed))
            {
                level = parsed;
            }

            return new ConsiderEvent(action[..i], attitude, level);
        }

        return null;
    }

    private static readonly (string Infix, string Attitude)[] ConsiderPatterns =
    [
        (" scowls at you, ready to attack", "scowl"),
        (" glares at you threateningly", "threatening"),
        (" glowers at you dubiously", "dubious"),
        (" regards you indifferently", "indifferent"),
        (" judges you amiably", "amiable"),
        (" kindly considers you", "kindly"),
        (" looks upon you warmly", "warm"),
        (" regards you as an ally", "ally"),
    ];

    private static GameEvent? ParseLoot(string action, ParserOptions options)
    {
        // "--You have looted a Cold-Forged Cudgel from Queen Dracnia's corpse.--"
        // "--Soandso has looted a Rusty Whip from a bandit's corpse.--"
        if (action.StartsWith("--", StringComparison.Ordinal) &&
            action.EndsWith(".--", StringComparison.Ordinal))
        {
            var body = action.AsSpan(2, action.Length - 5);
            string looter;
            if (body.StartsWith("You have looted a ", StringComparison.Ordinal))
            {
                looter = options.PlayerName;
                body = body["You have looted a ".Length..];
            }
            else
            {
                var i = body.IndexOf(" has looted a ", StringComparison.Ordinal);
                if (i <= 0)
                {
                    return null;
                }

                looter = body[..i].ToString();
                body = body[(i + " has looted a ".Length)..];
            }

            var from = body.LastIndexOf(" from ", StringComparison.Ordinal);
            return from > 0
                ? new LootEvent(looter, body[..from].ToString(), StripCorpse(body[(from + 6)..]))
                : null;
        }

        // "You looted a Froglok Meat from a froglok ton knight's corpse and
        // sold it for 5 copper." — auto-sell; "You looted 2 X …" for stacks.
        if (action.StartsWith("You looted ", StringComparison.Ordinal))
        {
            var body = action.AsSpan("You looted ".Length).TrimEnd();
            if (body.Length > 0 && body[^1] == '.')
            {
                body = body[..^1];
            }

            var sold = body.IndexOf(" and sold it for ", StringComparison.Ordinal);
            if (sold <= 0)
            {
                return null;
            }

            var copper = ParseCoins(body[(sold + " and sold it for ".Length)..]);
            if (copper is null)
            {
                return null;
            }

            body = body[..sold];
            var quantity = 1;
            if (body.StartsWith("a ", StringComparison.Ordinal))
            {
                body = body[2..];
            }
            else if (body.StartsWith("an ", StringComparison.Ordinal))
            {
                body = body[3..];
            }
            else
            {
                var digits = 0;
                while (digits < body.Length && char.IsAsciiDigit(body[digits]))
                {
                    digits++;
                }

                if (digits == 0 || digits >= body.Length || body[digits] != ' ' ||
                    !int.TryParse(body[..digits], out quantity))
                {
                    return null;
                }

                body = body[(digits + 1)..];
            }

            var from = body.LastIndexOf(" from ", StringComparison.Ordinal);
            return from > 0
                ? new LootEvent(options.PlayerName, body[..from].ToString(),
                    StripCorpse(body[(from + 6)..]), copper, quantity)
                : null;
        }

        // "You receive 1 platinum, 2 gold and 3 copper from the corpse." /
        // "You receive 4 gold and 1 silver as your split."
        if (action.StartsWith("You receive ", StringComparison.Ordinal))
        {
            var body = action.AsSpan("You receive ".Length).TrimEnd();
            if (body.Length > 0 && body[^1] == '.')
            {
                body = body[..^1];
            }

            string source;
            if (body.EndsWith(" from the corpse", StringComparison.Ordinal))
            {
                source = "corpse";
                body = body[..^" from the corpse".Length];
            }
            else if (body.EndsWith(" as your split", StringComparison.Ordinal))
            {
                source = "split";
                body = body[..^" as your split".Length];
            }
            else
            {
                return null;
            }

            var copper = ParseCoins(body);
            return copper is null
                ? null
                : new LootEvent(options.PlayerName, Item: null, source, copper);
        }

        return null;
    }

    /// <summary>"1 platinum, 2 gold, 5 silver and 3 copper" → total copper, or null on any mismatch.</summary>
    private static long? ParseCoins(ReadOnlySpan<char> text)
    {
        long total = 0;
        var any = false;
        while (text.Length > 0)
        {
            var digits = 0;
            while (digits < text.Length && char.IsAsciiDigit(text[digits]))
            {
                digits++;
            }

            if (digits == 0 || digits >= text.Length || text[digits] != ' ' ||
                !long.TryParse(text[..digits], out var amount))
            {
                return null;
            }

            text = text[(digits + 1)..];
            long multiplier;
            if (text.StartsWith("platinum", StringComparison.Ordinal))
            {
                multiplier = 1000;
                text = text["platinum".Length..];
            }
            else if (text.StartsWith("gold", StringComparison.Ordinal))
            {
                multiplier = 100;
                text = text["gold".Length..];
            }
            else if (text.StartsWith("silver", StringComparison.Ordinal))
            {
                multiplier = 10;
                text = text["silver".Length..];
            }
            else if (text.StartsWith("copper", StringComparison.Ordinal))
            {
                multiplier = 1;
                text = text["copper".Length..];
            }
            else
            {
                return null;
            }

            total += amount * multiplier;
            any = true;
            if (text.Length == 0)
            {
                break;
            }

            if (text.StartsWith(", ", StringComparison.Ordinal))
            {
                text = text[2..];
            }
            else if (text.StartsWith(" and ", StringComparison.Ordinal))
            {
                text = text[5..];
            }
            else
            {
                return null;
            }
        }

        return any ? total : null;
    }

    private static string StripCorpse(ReadOnlySpan<char> source)
    {
        if (source.EndsWith("'s corpse", StringComparison.Ordinal))
        {
            source = source[..^"'s corpse".Length];
        }

        return source.ToString();
    }

    private static GameEvent? ParseFaction(string action)
    {
        // "Your faction standing with Frogloks of Guk has been adjusted by -4."
        // Classic servers: "… got better." / "… got worse." /
        // "… could not possibly get any better." (standing already capped).
        const string prefix = "Your faction standing with ";
        if (!action.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = action.AsSpan(prefix.Length);
        var adjusted = rest.IndexOf(" has been adjusted by ", StringComparison.Ordinal);
        if (adjusted > 0)
        {
            var number = rest[(adjusted + " has been adjusted by ".Length)..].TrimEnd();
            if (number.Length > 0 && number[^1] == '.')
            {
                number = number[..^1];
            }

            return int.TryParse(number, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var delta)
                ? new FactionEvent(rest[..adjusted].ToString(), delta, Better: delta >= 0)
                : null;
        }

        foreach (var (suffix, better, capped) in FactionSuffixes)
        {
            if (rest.EndsWith(suffix, StringComparison.Ordinal))
            {
                var faction = rest[..^suffix.Length];
                return faction.Length > 0
                    ? new FactionEvent(faction.ToString(), Delta: null, better, capped)
                    : null;
            }
        }

        return null;
    }

    private static readonly (string Suffix, bool Better, bool Capped)[] FactionSuffixes =
    [
        (" got better.", true, false),
        (" got worse.", false, false),
        (" could not possibly get any better.", true, true),
        (" could not possibly get any worse.", false, true),
    ];

    private static GameEvent? ParseExperience(string action)
    {
        // "You gain experience! (5.472%)" / "You gain party experience! (1.812%)"
        // — modern servers append the level-progress delta; classic servers
        // write "You gain experience!!" with no number.
        if (action.StartsWith("You gain ", StringComparison.Ordinal))
        {
            var rest = action.AsSpan("You gain ".Length);
            var party = rest.StartsWith("party ", StringComparison.Ordinal);
            if (party)
            {
                rest = rest["party ".Length..];
            }

            if (!rest.StartsWith("experience!", StringComparison.Ordinal))
            {
                return null; // "You gain a rune for …" and friends
            }

            rest = rest["experience!".Length..].TrimStart('!').Trim();
            if (rest.Length == 0)
            {
                return new ExperienceEvent(Percent: null, party);
            }

            if (rest[0] == '(' && rest[^1] == ')' && rest.Length > 3 && rest[^2] == '%' &&
                double.TryParse(rest[1..^2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var percent))
            {
                return new ExperienceEvent(percent, party);
            }

            return null; // trailing text that isn't a percent: not this grammar
        }

        // "You have gained an ability point!  You now have 2 ability points."
        if (action.StartsWith("You have gained an ability point!", StringComparison.Ordinal))
        {
            int? total = null;
            var i = action.IndexOf("You now have ", StringComparison.Ordinal);
            if (i > 0)
            {
                var digits = action.AsSpan(i + "You now have ".Length);
                var end = 0;
                while (end < digits.Length && char.IsAsciiDigit(digits[end]))
                {
                    end++;
                }

                if (end > 0 && int.TryParse(digits[..end], out var parsed))
                {
                    total = parsed;
                }
            }

            return new ExperienceEvent(Percent: null, Party: false, AaPoint: true, AaTotal: total);
        }

        return null;
    }

    private static GameEvent? ParseMembership(string action, ParserOptions options)
    {
        foreach (var (pattern, raid, joined) in MembershipPatterns)
        {
            if (action.StartsWith("You have", StringComparison.Ordinal) &&
                action == "You have" + pattern)
            {
                return new MembershipEvent(options.PlayerName, raid, joined);
            }

            var i = action.IndexOf(" has" + pattern, StringComparison.Ordinal);
            if (i > 0)
            {
                return new MembershipEvent(action[..i], raid, joined);
            }
        }

        var leader = action.IndexOf(" is now the leader of your raid.", StringComparison.Ordinal);
        if (leader > 0)
        {
            return new MembershipEvent(action[..leader], Raid: true, Joined: true);
        }

        return null;
    }

    private static readonly (string Pattern, bool Raid, bool Joined)[] MembershipPatterns =
    [
        (" joined the raid.", true, true),
        (" joined the group.", false, true),
        (" left the raid.", true, false),
        (" left the group.", false, false),
    ];

    private static GameEvent? ParseWho(string action)
    {
        if (action.Length < 6 || action[0] != '[')
        {
            return null;
        }

        var close = action.IndexOf("] ", StringComparison.Ordinal);
        if (close < 2)
        {
            return null;
        }

        var inside = action[1..close];
        var rest = action.AsSpan(close + 2);
        var nameEnd = rest.IndexOf(' ');
        var name = (nameEnd < 0 ? rest : rest[..nameEnd]).ToString();
        if (name.Length < 3 || !char.IsAsciiLetterUpper(name[0]))
        {
            return null;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetter(c))
            {
                return null;
            }
        }

        if (inside == "ANONYMOUS")
        {
            return new WhoEvent(name, null, null);
        }

        var space = inside.IndexOf(' ');
        if (space <= 0 || !int.TryParse(inside.AsSpan(0, space), out var level))
        {
            return null;
        }

        return new WhoEvent(name, level, inside[(space + 1)..]);
    }

    private static GameEvent? ParseTaunt(string action, ParserOptions options)
    {
        const string Attention = "'s attention!";
        if (action.EndsWith(Attention, StringComparison.Ordinal))
        {
            if (action.StartsWith("You capture ", StringComparison.Ordinal))
            {
                var target = action["You capture ".Length..^Attention.Length];
                return new TauntEvent(options.PlayerName, Names.CapitalizeFirst(target), Success: true);
            }

            var i = action.IndexOf(" has captured ", StringComparison.Ordinal);
            if (i > 0)
            {
                var target = action[(i + " has captured ".Length)..^Attention.Length];
                return new TauntEvent(Names.CapitalizeFirst(action[..i]), Names.CapitalizeFirst(target), Success: true);
            }

            return null;
        }

        var failed = action.IndexOf(" failed to taunt ", StringComparison.Ordinal);
        if (failed > 0)
        {
            var target = action.AsSpan(failed + " failed to taunt ".Length).TrimEnd();
            if (target.Length > 0 && target[^1] == '.')
            {
                target = target[..^1];
            }

            return new TauntEvent(
                Names.Resolve(action[..failed], options),
                Names.CapitalizeFirst(target.ToString()),
                Success: false);
        }

        const string Improved = " due to an improved taunt.";
        const string Focused = " is focused on attacking ";
        var focused = action.IndexOf(Focused, StringComparison.Ordinal);
        if (focused > 0 && action.EndsWith(Improved, StringComparison.Ordinal))
        {
            var start = focused + Focused.Length;
            var end = action.Length - Improved.Length;
            if (end <= start)
            {
                return null; // no taunter between the two fixed parts
            }

            var taunter = action[start..end];
            return new TauntEvent(
                Names.Resolve(taunter, options),
                Names.CapitalizeFirst(action[..focused]),
                Success: true,
                Improved: true);
        }

        return null;
    }

    private static GameEvent? ParseZone(string action)
    {
        if (action is "LOADING, PLEASE WAIT..." or "Welcome to EverQuest!")
        {
            return new ZoneEvent(null);
        }

        if (action.StartsWith("You have entered ", StringComparison.Ordinal))
        {
            var name = action.AsSpan("You have entered ".Length).TrimEnd();
            if (name.Length > 0 && name[^1] == '.')
            {
                name = name[..^1];
            }

            // "You have entered an area where levitation is not allowed." and
            // similar restriction notices are not zone changes.
            if (name.Contains("area where", StringComparison.Ordinal))
            {
                return null;
            }

            return name.Length == 0 ? null : new ZoneEvent(name.ToString());
        }

        return null;
    }

    private static GameEvent? ParseResist(string action, ParserOptions options)
    {
        const string YourTarget = "Your target resisted the ";
        const string SpellSuffix = " spell.";
        if (action.StartsWith(YourTarget, StringComparison.Ordinal) &&
            action.EndsWith(SpellSuffix, StringComparison.Ordinal))
        {
            // Same overlap trap as the interrupt grammar: with no spell named,
            // the prefix and suffix meet and the slice would run backwards.
            var end = action.Length - SpellSuffix.Length;
            return end <= YourTarget.Length
                ? null
                : new ResistEvent(options.PlayerName, null, action[YourTarget.Length..end]);
        }

        var i = action.IndexOf(" resisted your ", StringComparison.Ordinal);
        if (i > 0)
        {
            var spell = action.AsSpan(i + " resisted your ".Length).TrimEnd();
            if (spell.Length > 0 && (spell[^1] == '!' || spell[^1] == '.'))
            {
                spell = spell[..^1];
            }

            return spell.Length == 0
                ? null
                : new ResistEvent(options.PlayerName, Names.CapitalizeFirst(action[..i]), spell.ToString());
        }

        return null;
    }
}
