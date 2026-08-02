using EQDeeps.Core.Events;

namespace EQDeeps.Core.Parsing;

/// <summary>
/// Spell-casting activity: cast starts (casting/singing), interrupts, fizzles,
/// activated abilities, and the wear-off messages that carry a real spell name
/// ("Your X spell has worn off [of Soandso]."). "Lands on" messages and
/// received-buff fades use per-spell emote text instead of the name, so those
/// need the spell database and are resolved in a later layer, not here.
/// </summary>
public static class CastParser
{
    public static GameEvent? Parse(string action, ParserOptions options)
    {
        if (action.StartsWith("You begin casting ", StringComparison.Ordinal))
        {
            return Begin(options.PlayerName, action.AsSpan("You begin casting ".Length), song: false);
        }

        if (action.StartsWith("You begin singing ", StringComparison.Ordinal))
        {
            return Begin(options.PlayerName, action.AsSpan("You begin singing ".Length), song: true);
        }

        var i = action.IndexOf(" begins casting ", StringComparison.Ordinal);
        if (i > 0)
        {
            return Begin(action[..i], action.AsSpan(i + " begins casting ".Length), song: false);
        }

        i = action.IndexOf(" begins singing ", StringComparison.Ordinal);
        if (i > 0)
        {
            return Begin(action[..i], action.AsSpan(i + " begins singing ".Length), song: true);
        }

        // Older format: "<caster> begins to cast a spell. <Spell Name>"
        i = action.IndexOf(" begins to cast a spell.", StringComparison.Ordinal);
        if (i > 0)
        {
            var rest = action.AsSpan(i + " begins to cast a spell.".Length).Trim();
            return new CastEvent(
                Names.CapitalizeFirst(action[..i]),
                rest.Length > 0 ? rest.ToString() : null,
                CastKind.Begin);
        }

        // "Your Burst of Flames spell is interrupted."
        if (action.StartsWith("Your ", StringComparison.Ordinal) &&
            action.EndsWith(" spell is interrupted.", StringComparison.Ordinal))
        {
            var spell = action["Your ".Length..^" spell is interrupted.".Length];
            return new CastEvent(options.PlayerName, spell.Length > 0 ? spell : null, CastKind.Interrupted);
        }

        i = action.IndexOf("'s casting is interrupted!", StringComparison.Ordinal);
        if (i > 0)
        {
            return new CastEvent(Names.CapitalizeFirst(action[..i]), null, CastKind.Interrupted);
        }

        if (action.StartsWith("Your spell fizzles!", StringComparison.Ordinal))
        {
            return new CastEvent(options.PlayerName, null, CastKind.Fizzle);
        }

        i = action.IndexOf("'s spell fizzles!", StringComparison.Ordinal);
        if (i > 0)
        {
            return new CastEvent(Names.CapitalizeFirst(action[..i]), null, CastKind.Fizzle);
        }

        if (action.StartsWith("Your ", StringComparison.Ordinal))
        {
            // "Your Aegolism spell has worn off of Soandso."
            i = action.IndexOf(" spell has worn off of ", StringComparison.Ordinal);
            if (i > "Your ".Length && action.EndsWith(".", StringComparison.Ordinal))
            {
                var target = action[(i + " spell has worn off of ".Length)..^1];
                if (target.Length > 0)
                {
                    return new WearOffEvent(action["Your ".Length..i], Names.CapitalizeFirst(target));
                }
            }

            // "Your Spirit of Wolf spell has worn off."
            if (action.EndsWith(" spell has worn off.", StringComparison.Ordinal) &&
                action.Length > "Your ".Length + " spell has worn off.".Length)
            {
                return new WearOffEvent(
                    action["Your ".Length..^" spell has worn off.".Length], options.PlayerName);
            }
        }

        // "You activate Rest." / "Soandso activates Rest."
        if (action.StartsWith("You activate ", StringComparison.Ordinal) &&
            action.EndsWith(".", StringComparison.Ordinal))
        {
            return new AbilityEvent(options.PlayerName, action["You activate ".Length..^1]);
        }

        i = action.IndexOf(" activates ", StringComparison.Ordinal);
        if (i > 0 && action.EndsWith(".", StringComparison.Ordinal) &&
            i + " activates ".Length < action.Length - 1)
        {
            return new AbilityEvent(
                Names.CapitalizeFirst(action[..i]), action[(i + " activates ".Length)..^1]);
        }

        return null;
    }

    private static CastEvent Begin(string caster, ReadOnlySpan<char> spell, bool song)
    {
        spell = spell.TrimEnd();
        if (spell.Length > 0 && spell[^1] == '.')
        {
            spell = spell[..^1];
        }

        return new CastEvent(Names.CapitalizeFirst(caster), spell.Length > 0 ? spell.ToString() : null, CastKind.Begin, song);
    }
}
