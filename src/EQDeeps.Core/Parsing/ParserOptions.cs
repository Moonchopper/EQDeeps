using EQDeeps.Core.Spells;

namespace EQDeeps.Core.Parsing;

/// <summary>
/// Per-session parser configuration. <paramref name="PlayerName"/> is the log
/// owner (from the filename); every You/YOUR/yourself reference resolves to it.
/// <paramref name="EmuMode"/> enables the older EMU-server grammars (flipped DoT
/// ordering, separate critical-hit lines, (Owner: X) pet annotations).
/// <paramref name="Spells"/> is the player's own spell files, which turn the
/// per-spell emotes ("Your wounds begin to heal.") back into spells; it is
/// empty when the log has no game install beside it, and every grammar that
/// uses it then simply does not match.
///
/// <para>Parsing stays a pure function of the line: the book is configuration
/// chosen once per session, exactly like the player's name.</para>
/// </summary>
public sealed record ParserOptions(string PlayerName, bool EmuMode = false)
{
    public SpellBook Spells { get; init; } = SpellBook.Empty;
}
