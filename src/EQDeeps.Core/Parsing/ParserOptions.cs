namespace EQDeeps.Core.Parsing;

/// <summary>
/// Per-session parser configuration. <paramref name="PlayerName"/> is the log
/// owner (from the filename); every You/YOUR/yourself reference resolves to it.
/// <paramref name="EmuMode"/> enables the older EMU-server grammars (flipped DoT
/// ordering, separate critical-hit lines, (Owner: X) pet annotations).
/// </summary>
public sealed record ParserOptions(string PlayerName, bool EmuMode = false);
