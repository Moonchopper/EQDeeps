using EQDeeps.Core.Events;

namespace EQDeeps.Core.Parsing;

/// <summary>
/// Spell-casting activity: cast starts (casting/singing), interrupts, fizzles,
/// activated abilities, and the wear-off messages that carry a real spell name
/// ("Your X spell has worn off [of Soandso]."). "Lands on" messages and
/// received-buff fades use per-spell emote text instead of the name; those are
/// resolved here too when the session has the player's spell files
/// (<see cref="ParserOptions.Spells"/>), and skipped when it does not.
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

        // "Your Burst of Flames spell is interrupted." — and the nameless
        // "Your spell is interrupted.", where the game omits the spell and the
        // two fixed parts share their space. That line is common (thousands per
        // log) and used to take the whole session down with it, so the bound
        // here is load-bearing, not defensive dressing.
        const string Your = "Your ";
        const string Interrupted = " spell is interrupted.";
        if (action.StartsWith(Your, StringComparison.Ordinal) &&
            action.EndsWith(Interrupted, StringComparison.Ordinal))
        {
            var end = action.Length - Interrupted.Length;
            return new CastEvent(
                options.PlayerName,
                end > Your.Length ? action[Your.Length..end] : null,
                CastKind.Interrupted);
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

        // Emote lines, last: they are matched against the player's own spell
        // files rather than a grammar, so anything with a shape of its own has
        // already had its chance above.
        if (!options.Spells.IsEmpty)
        {
            if (options.Spells.TryLandsOnYou(action, out var onYou))
            {
                return new LandedEvent(options.PlayerName, onYou.Spell, action, onYou.Candidates);
            }

            if (options.Spells.TryFade(action, out var fade))
            {
                // A fade whose text names one spell is the same event a
                // "Your X spell has worn off." line produces; an ambiguous one
                // would have to invent a name, so it is left alone rather than
                // filed under a guess.
                if (fade.Spell is { } faded)
                {
                    return new WearOffEvent(faded, options.PlayerName);
                }
            }

            if (options.Spells.TryLandsOnOther(action, out var target, out var onOther))
            {
                return new LandedEvent(Names.CapitalizeFirst(target), onOther.Spell, action, onOther.Candidates);
            }
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
