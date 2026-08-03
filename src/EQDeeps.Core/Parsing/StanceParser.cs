using EQDeeps.Core.Events;

namespace EQDeeps.Core.Parsing;

/// <summary>
/// Combat stance switches — the exclusive "how am I fighting right now" state.
///
/// Deliberately shape-based rather than a list of known stances. Servers keep
/// adding them (defensive, berserker, precision, …) and the classic disciplines
/// word the same idea as a "fighting style"; enumerating them would mean a code
/// change every time one is introduced, and an unknown stance would silently
/// vanish from the parse rather than showing up as itself.
/// </summary>
public static class StanceParser
{
    private const string StanceSuffix = " stance.";
    private const string StyleSuffix = " fighting style.";

    /// <summary>The state before the first switch is seen — not a stance the game names.</summary>
    public const string Unknown = "(no stance)";

    /// <summary>
    /// Longest a stance name may be. Stances are one or two words; a longer
    /// capture means the sentence merely ended in "stance" and is not a switch
    /// ("the guards take up a defensive stance" and other flavour text).
    /// </summary>
    private const int MaxNameLength = 32;

    public static GameEvent? Parse(string action, ParserOptions options)
    {
        // "You begin to change your stance." — the switch is announced a beat
        // before it lands. The state moves on the "assume" line, not this one.
        if (action.EndsWith("change your stance.", StringComparison.Ordinal))
        {
            return null;
        }

        ReadOnlySpan<char> head;
        if (action.EndsWith(StanceSuffix, StringComparison.Ordinal))
        {
            head = action.AsSpan(0, action.Length - StanceSuffix.Length);
        }
        else if (action.EndsWith(StyleSuffix, StringComparison.Ordinal))
        {
            head = action.AsSpan(0, action.Length - StyleSuffix.Length);
        }
        else
        {
            return null;
        }

        // "You return to your normal stance." / "You resume your normal stance."
        // Dropping a stance is a state change like any other, so it gets a
        // named span rather than a hole in the timeline.
        if (head is "You return to your normal" or "You resume your normal" or
            "You return to your normal fighting")
        {
            return new StanceEvent(options.PlayerName, "Normal");
        }

        var (player, name) = Split(head, options);
        if (player is null || name.Length == 0 || name.Length > MaxNameLength)
        {
            return null;
        }

        return new StanceEvent(player, Names.CapitalizeFirst(name));
    }

    /// <summary>
    /// Splits "You assume a defensive" / "Soandso assumes an evasive" into the
    /// actor and the stance name. Returns a null actor when the sentence is not
    /// a stance switch.
    /// </summary>
    private static (string? Player, string Name) Split(ReadOnlySpan<char> head, ParserOptions options)
    {
        const string You = "You assume ";
        if (head.StartsWith(You, StringComparison.Ordinal))
        {
            return (options.PlayerName, Article(head[You.Length..]));
        }

        var i = head.IndexOf(" assumes ", StringComparison.Ordinal);
        return i > 0
            ? (Names.CapitalizeFirst(head[..i].ToString()), Article(head[(i + " assumes ".Length)..]))
            : (null, string.Empty);
    }

    /// <summary>Drops the article the game puts in front of the stance name.</summary>
    private static string Article(ReadOnlySpan<char> text)
    {
        if (text.StartsWith("a ", StringComparison.Ordinal))
        {
            text = text[2..];
        }
        else if (text.StartsWith("an ", StringComparison.Ordinal))
        {
            text = text[3..];
        }
        else if (text.StartsWith("the ", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        return text.Trim().ToString();
    }
}
