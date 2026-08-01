using EQDeeps.Core.Events;

namespace EQDeeps.Core.Parsing;

/// <summary>
/// Chat-channel grammars. Chat runs before every combat grammar because quoted
/// text is arbitrary player input that can mimic combat lines; once a message is
/// classified as chat, processing stops. The earliest channel-clause match in the
/// line is authoritative, so quoted imitations after a real clause cannot win.
/// </summary>
public static class ChatParser
{
    public static ChatEvent? Parse(string action, ParserOptions options)
    {
        if (action.Length < 5)
        {
            return null;
        }

        if (action.StartsWith("You ", StringComparison.Ordinal))
        {
            return ParseSelf(action, options);
        }

        return ParseOther(action, options) ?? ParseTellWindowEcho(action);
    }

    // ---- self (log owner) forms -------------------------------------------

    private static readonly (string Prefix, ChatChannel Channel)[] SelfQuoted =
    [
        ("You say out of character,", ChatChannel.Ooc),
        ("You say to your guild,", ChatChannel.Guild),
        ("You say to your fellowship,", ChatChannel.Fellowship),
        ("You say,", ChatChannel.Say),
        ("You auction,", ChatChannel.Auction),
        ("You shout,", ChatChannel.Shout),
        ("You tell your party,", ChatChannel.Group),
        ("You tell your raid,", ChatChannel.Raid),
    ];

    private static ChatEvent? ParseSelf(string action, ParserOptions options)
    {
        foreach (var (prefix, channel) in SelfQuoted)
        {
            if (action.StartsWith(prefix, StringComparison.Ordinal))
            {
                var text = ExtractQuoted(action, prefix.Length);
                return text is null ? null : new ChatEvent(channel, options.PlayerName, text);
            }
        }

        if (action.StartsWith("You told ", StringComparison.Ordinal))
        {
            // "You told <name>, 'hello'" — also degenerate "You told <name> ''"
            // and queued forms without the comma.
            var rest = action.AsSpan("You told ".Length);
            var end = rest.IndexOfAny(',', ' ', '\'');
            if (end <= 0)
            {
                return null;
            }

            var receiver = Names.StripServerPrefix(rest[..end].ToString());
            var from = "You told ".Length + end;
            if (from < action.Length && action[from] == ',')
            {
                from++;
            }

            var text = ExtractQuoted(action, from) ?? string.Empty;
            return new ChatEvent(ChatChannel.Tell, options.PlayerName, text, receiver);
        }

        if (action.StartsWith("You tell ", StringComparison.Ordinal))
        {
            // "You tell <channel>[:member], 'hello'" — a named user channel.
            var rest = action.AsSpan("You tell ".Length);
            var comma = rest.IndexOf(',');
            if (comma <= 0)
            {
                return null;
            }

            var channelName = NormalizeChannelName(rest[..comma]);
            var text = ExtractQuoted(action, "You tell ".Length + comma + 1);
            return text is null
                ? null
                : new ChatEvent(ChatChannel.Custom, options.PlayerName, text, CustomChannel: channelName);
        }

        return null;
    }

    // ---- other-sender forms -----------------------------------------------

    private enum Clause
    {
        SayQuoted,
        Ooc,
        SayNoComma,
        Auction,
        Shout,
        Group,
        Guild,
        Raid,
        Fellowship,
        TellYou,
        ToldYou,
        TellOther,
    }

    private static readonly (string Pattern, Clause Clause)[] OtherClauses =
    [
        (" says out of character,", Clause.Ooc),
        (" says,", Clause.SayQuoted),
        (" says '", Clause.SayNoComma),
        (" auctions,", Clause.Auction),
        (" shouts,", Clause.Shout),
        (" tells the group,", Clause.Group),
        (" tells the guild,", Clause.Guild),
        (" tells the raid,", Clause.Raid),
        (" tells the fellowship,", Clause.Fellowship),
        (" tells you,", Clause.TellYou),
        (" told you,", Clause.ToldYou),
        (" tells ", Clause.TellOther),
    ];

    private static ChatEvent? ParseOther(string action, ParserOptions options)
    {
        // Earliest clause wins; at equal positions the longer pattern wins
        // (" tells you," beats " tells ").
        var bestIndex = int.MaxValue;
        var bestLength = 0;
        Clause bestClause = default;
        foreach (var (pattern, clause) in OtherClauses)
        {
            var i = action.IndexOf(pattern, StringComparison.Ordinal);
            if (i > 0 && (i < bestIndex || (i == bestIndex && pattern.Length > bestLength)))
            {
                bestIndex = i;
                bestLength = pattern.Length;
                bestClause = clause;
            }
        }

        if (bestIndex == int.MaxValue)
        {
            return null;
        }

        var sender = Names.StripServerPrefix(action[..bestIndex]);
        var afterClause = bestIndex + bestLength;

        switch (bestClause)
        {
            case Clause.SayNoComma:
            {
                // "<pet> says 'My leader is X'" — still chat; identity layers
                // read the pet-leader fact out of Say text downstream.
                var text = ExtractQuoted(action, afterClause - 1);
                return text is null ? null : new ChatEvent(ChatChannel.Say, sender, text);
            }

            case Clause.TellOther:
            {
                var rest = action.AsSpan(afterClause);
                var comma = rest.IndexOf(',');
                if (comma <= 0)
                {
                    return null;
                }

                var channelName = NormalizeChannelName(rest[..comma]);
                var text = ExtractQuoted(action, afterClause + comma + 1);
                return text is null
                    ? null
                    : new ChatEvent(ChatChannel.Custom, sender, text, CustomChannel: channelName);
            }

            case Clause.TellYou:
            case Clause.ToldYou:
            {
                var text = ExtractQuoted(action, afterClause);
                return text is null
                    ? null
                    : new ChatEvent(ChatChannel.Tell, sender, text, options.PlayerName);
            }

            default:
            {
                var channel = bestClause switch
                {
                    Clause.SayQuoted => ChatChannel.Say,
                    Clause.Ooc => ChatChannel.Ooc,
                    Clause.Auction => ChatChannel.Auction,
                    Clause.Shout => ChatChannel.Shout,
                    Clause.Group => ChatChannel.Group,
                    Clause.Guild => ChatChannel.Guild,
                    Clause.Raid => ChatChannel.Raid,
                    _ => ChatChannel.Fellowship,
                };
                var text = ExtractQuoted(action, afterClause);
                return text is null ? null : new ChatEvent(channel, sender, text);
            }
        }
    }

    private static ChatEvent? ParseTellWindowEcho(string action)
    {
        // "Sender -> Receiver: text" — tell-window echo, no quotes.
        var arrow = action.IndexOf(" -> ", StringComparison.Ordinal);
        if (arrow <= 0)
        {
            return null;
        }

        var colon = action.IndexOf(':', arrow + 4);
        if (colon < 0)
        {
            return null;
        }

        var sender = action[..arrow];
        var receiver = action[(arrow + 4)..colon];
        if (sender.Contains(' ') || receiver.Contains(' '))
        {
            return null;
        }

        var text = colon + 2 <= action.Length ? action[Math.Min(colon + 2, action.Length)..] : string.Empty;
        return new ChatEvent(ChatChannel.Tell, Names.StripServerPrefix(sender), text, Names.StripServerPrefix(receiver));
    }

    // ---- helpers ----------------------------------------------------------

    /// <summary>
    /// Extracts the quoted message text starting the scan at <paramref name="from"/>:
    /// skips spaces and an optional "in an unknown tongue," clause, then takes the
    /// text between the opening quote and the trailing quote (or end of line).
    /// Returns null when no opening quote exists — the line is not this chat form.
    /// </summary>
    private static string? ExtractQuoted(string action, int from)
    {
        var i = from;
        while (i < action.Length && action[i] == ' ')
        {
            i++;
        }

        const string UnknownTongue = "in an unknown tongue,";
        if (action.AsSpan(i).StartsWith(UnknownTongue, StringComparison.Ordinal))
        {
            i += UnknownTongue.Length;
            while (i < action.Length && action[i] == ' ')
            {
                i++;
            }
        }

        if (i >= action.Length || action[i] != '\'')
        {
            return null;
        }

        var start = i + 1;
        var end = action.Length;
        if (end > start && action[end - 1] == '\'')
        {
            end--;
        }

        return start > end ? string.Empty : action[start..end];
    }

    private static string NormalizeChannelName(ReadOnlySpan<char> token)
    {
        // Strip a trailing ":<memberNumber>" and lowercase for a stable key.
        var colon = token.IndexOf(':');
        if (colon > 0)
        {
            token = token[..colon];
        }

        return token.ToString().ToLowerInvariant();
    }
}
