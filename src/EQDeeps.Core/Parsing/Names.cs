namespace EQDeeps.Core.Parsing;

/// <summary>
/// Name normalization shared by the grammars. NPC names appear with lowercase
/// articles in object position ("an abyssal terror") and capitalized in subject
/// position; we normalize to the capitalized form so both map to one identity.
/// </summary>
public static class Names
{
    public static string CapitalizeFirst(string name) =>
        name.Length > 0 && char.IsLower(name[0])
            ? string.Concat(char.ToUpperInvariant(name[0]).ToString(), name.AsSpan(1))
            : name;

    /// <summary>True for the log owner's self-references in any case form the game uses.</summary>
    public static bool IsYou(ReadOnlySpan<char> word) =>
        word is "You" or "YOU" or "you";

    public static bool IsYour(ReadOnlySpan<char> word) =>
        word is "Your" or "YOUR" or "your";

    public static bool IsSelfPronoun(ReadOnlySpan<char> word) =>
        word is "himself" or "herself" or "itself" or "yourself";

    /// <summary>Resolves self-references to the player name, otherwise normalizes capitalization.</summary>
    public static string Resolve(string name, ParserOptions options) =>
        IsYou(name) ? options.PlayerName : CapitalizeFirst(name);

    /// <summary>
    /// Reduces a cross-server qualified name (Server.Name) to the character name.
    /// The character name is the segment after the dot.
    /// </summary>
    public static string StripServerPrefix(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }
}
