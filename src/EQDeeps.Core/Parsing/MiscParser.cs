using EQDeeps.Core.Events;

namespace EQDeeps.Core.Parsing;

/// <summary>Taunts, zone transitions, spell resists, membership, and /who lines.</summary>
public static class MiscParser
{
    public static GameEvent? Parse(string action, ParserOptions options)
    {
        return ParseTaunt(action, options)
            ?? ParseZone(action)
            ?? ParseResist(action, options)
            ?? ParseMembership(action, options)
            ?? ParseWho(action);
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
        var focused = action.IndexOf(" is focused on attacking ", StringComparison.Ordinal);
        if (focused > 0 && action.EndsWith(Improved, StringComparison.Ordinal))
        {
            var taunter = action[(focused + " is focused on attacking ".Length)..^Improved.Length];
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
            var spell = action[YourTarget.Length..^SpellSuffix.Length];
            return spell.Length == 0 ? null : new ResistEvent(options.PlayerName, null, spell);
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
