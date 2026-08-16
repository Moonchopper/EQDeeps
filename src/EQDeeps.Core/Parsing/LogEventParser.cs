using EQDeeps.Core.Events;

namespace EQDeeps.Core.Parsing;

/// <summary>
/// Turns one log message (the text after the timestamp prefix) into a typed
/// <see cref="GameEvent"/>, or null when the line is unhandled noise.
///
/// Instance-based on purpose: the only mutable state is the one-line EMU crit
/// lookbehind, owned per session so concurrent sessions never share parser state.
/// Chat runs first — quoted player text can mimic any combat grammar, and a chat
/// classification terminates processing.
/// </summary>
public sealed class LogEventParser
{
    private readonly ParserOptions _options;
    private readonly DamageParser.State _damageState = new();

    public LogEventParser(ParserOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// The one piece of state that outlives a line: an EMU crit announcement
    /// waiting for the hit it applies to. Exposed so a checkpoint can carry
    /// it across runs — a resume that lands between the announcement and the
    /// hit would otherwise lose the crit on that one hit.
    /// </summary>
    public string? PendingEmuCritAttacker
    {
        get => _damageState.PendingEmuCritAttacker;
        set => _damageState.PendingEmuCritAttacker = value;
    }

    public GameEvent? Parse(string action) => Parse(action, out _);

    /// <summary>
    /// <paramref name="recognized"/> is true when the line matched some grammar,
    /// including grammars that deliberately record nothing (defender ripostes, EMU
    /// crit announcements). Unrecognized lines are counted, never thrown on.
    /// </summary>
    public GameEvent? Parse(string action, out bool recognized)
    {
        recognized = false;
        if (string.IsNullOrEmpty(action) || action.Length < 3)
        {
            return null;
        }

        var chat = ChatParser.Parse(action, _options);
        if (chat is not null)
        {
            recognized = true;
            return chat;
        }

        var damage = DamageParser.Parse(action, _options, _damageState, out var damageConsumed);
        if (damage is not null || damageConsumed)
        {
            recognized = true;
            return damage;
        }

        var heal = HealParser.Parse(action, _options);
        if (heal is not null)
        {
            recognized = true;
            return heal;
        }

        var death = DeathParser.Parse(action, _options);
        if (death is not null)
        {
            recognized = true;
            return death;
        }

        var cast = CastParser.Parse(action, _options);
        if (cast is not null)
        {
            recognized = true;
            return cast;
        }

        var stance = StanceParser.Parse(action, _options);
        if (stance is not null)
        {
            recognized = true;
            return stance;
        }

        var misc = MiscParser.Parse(action, _options);
        if (misc is not null)
        {
            recognized = true;
            return misc;
        }

        return null;
    }
}
