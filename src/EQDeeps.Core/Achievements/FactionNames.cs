namespace EQDeeps.Core.Achievements;

/// <summary>
/// Joins a faction's three spellings to one comparison key — the achievements export, the faction
/// export and the reference layer each write the same faction differently, and none of them number
/// it (F35, ADR-023 Decision 5). Case, backticks and apostrophes, one leading "The", and all
/// whitespace are folded away mechanically; two pairs need a hand-written alias because no
/// mechanical rule finds them ("Coalition of Tradesfolk" / "…Tradefolk", "Corrupt Qeynos Guard" /
/// "…Guards"). Measured against the owner's 40 protected factions: 36 reach the faction file on the
/// mechanical rule alone, 40 of 40 with the two aliases below.
///
/// <para><b>Nothing fuzzier than this.</b> "Coalition of TradeFolk III" must not fold onto
/// "Coalition of Tradefolk" — it is a different faction, not a spelling of this one — so the rule
/// stops at case, punctuation and whitespace plus two named exceptions, and never at "close enough".</para>
/// </summary>
public static class FactionNames
{
    // Keyed and valued by the mechanical rule's own output, so an alias reads as "these two
    // mechanical keys are the same faction" and nothing more — neither side is normalised again.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["coalitionoftradesfolk"] = "coalitionoftradefolk",
        ["corruptqeynosguard"] = "corruptqeynosguards",
    };

    public static string Key(string name)
    {
        var s = name.ToLowerInvariant().Replace("`", "").Replace("'", "");

        // The leading "the " has to go before whitespace is stripped, or "the freeport militia"
        // would no longer carry the space this check needs to tell it apart from "theater".
        const string leadingThe = "the ";
        if (s.StartsWith(leadingThe, StringComparison.Ordinal))
        {
            s = s[leadingThe.Length..];
        }

        var key = string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
        return Aliases.TryGetValue(key, out var aliased) ? aliased : key;
    }
}
