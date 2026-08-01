using EQDeeps.Core.Events;

namespace EQDeeps.Core.Parsing;

/// <summary>
/// Death grammars. Slain lines are death events only — the killing blow's damage
/// arrives on its own hit line, so no damage record is emitted here.
/// </summary>
public static class DeathParser
{
    public static DeathEvent? Parse(string action, ParserOptions options)
    {
        if (action.StartsWith("You have slain ", StringComparison.Ordinal))
        {
            var victim = TrimBang(action.AsSpan("You have slain ".Length));
            return victim.Length == 0 ? null : new DeathEvent(Names.CapitalizeFirst(victim), options.PlayerName);
        }

        if (action.StartsWith("You have been slain by ", StringComparison.Ordinal))
        {
            var killer = TrimBang(action.AsSpan("You have been slain by ".Length));
            return killer.Length == 0 ? null : new DeathEvent(options.PlayerName, Names.CapitalizeFirst(killer));
        }

        var i = action.IndexOf(" has been slain by ", StringComparison.Ordinal);
        if (i > 0)
        {
            var killer = TrimBang(action.AsSpan(i + " has been slain by ".Length));
            return new DeathEvent(
                Names.CapitalizeFirst(action[..i]),
                killer.Length == 0 ? null : Names.CapitalizeFirst(killer));
        }

        i = action.IndexOf(" was slain by ", StringComparison.Ordinal);
        if (i > 0)
        {
            var killer = TrimBang(action.AsSpan(i + " was slain by ".Length));
            return new DeathEvent(
                Names.CapitalizeFirst(action[..i]),
                killer.Length == 0 ? null : Names.CapitalizeFirst(killer));
        }

        if (action.EndsWith(" died.", StringComparison.Ordinal))
        {
            var victim = action[..^" died.".Length];
            return victim.Length == 0 ? null : new DeathEvent(Names.CapitalizeFirst(victim), null);
        }

        return null;
    }

    private static string TrimBang(ReadOnlySpan<char> text)
    {
        text = text.TrimEnd();
        if (text.Length > 0 && (text[^1] == '!' || text[^1] == '.'))
        {
            text = text[..^1];
        }

        return text.ToString();
    }
}
