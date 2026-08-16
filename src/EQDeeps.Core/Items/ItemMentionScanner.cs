namespace EQDeeps.Core.Items;

/// <summary>
/// Finds known item names inside free text — chat, mostly. On EverQuest
/// Legends a linked item reaches the log as plain words (no <c>\x12</c>
/// payload, see <c>eq-log-format.md</c> §3.1), so "which items were named in
/// chat" is a dictionary match against what the registry already knows, and
/// an item nobody has looted, sold, bought or filtered is invisible here.
/// That is the honest limit of the source; ADR-019 records why no other
/// dictionary is bundled.
///
/// <para>Matching is by whole words, longest name first at any position, so
/// "Fine Steel Rapier" is one mention and not also "Fine Steel". Multi-word
/// names match regardless of case; a one-word name has to appear with its
/// own capitalisation ("Egg", not "egg"), because a one-word item name is
/// usually also an English word and a chat log is full of those — and not
/// beside another capitalised word, because "Horn" inside "Efreeti War Horn"
/// is a different item that this server has simply never seen.</para>
///
/// <para>Built once per registry version and reused across a scan; building
/// is a pass over the names, scanning is a pass over the text's words.</para>
/// </summary>
public sealed class ItemMentionScanner
{
    private readonly Dictionary<string, List<Candidate>> _byFirstWord = new(StringComparer.OrdinalIgnoreCase);

    public ItemMentionScanner(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var words = Words(name);
            if (words.Length == 0)
            {
                continue;
            }

            if (!_byFirstWord.TryGetValue(words[0], out var list))
            {
                list = [];
                _byFirstWord[words[0]] = list;
            }

            list.Add(new Candidate(name, words));
        }

        foreach (var list in _byFirstWord.Values)
        {
            // Longest first, so the first candidate that fits is the best one.
            list.Sort((a, b) => b.Words.Length.CompareTo(a.Words.Length));
        }
    }

    public bool IsEmpty => _byFirstWord.Count == 0;

    /// <summary>The distinct item names found in the text, in order of first appearance.</summary>
    public IReadOnlyList<string> Find(string text)
    {
        if (_byFirstWord.Count == 0 || string.IsNullOrEmpty(text))
        {
            return [];
        }

        var words = Words(text);
        List<string>? found = null;
        for (var i = 0; i < words.Length; i++)
        {
            if (!_byFirstWord.TryGetValue(words[i], out var candidates))
            {
                continue;
            }

            foreach (var c in candidates)
            {
                if (i + c.Words.Length > words.Length || !Matches(words, i, c))
                {
                    continue;
                }

                found ??= [];
                if (!found.Contains(c.Name))
                {
                    found.Add(c.Name);
                }

                i += c.Words.Length - 1;
                break;
            }
        }

        return found ?? [];
    }

    private static bool Matches(string[] words, int at, Candidate c)
    {
        var comparison = c.Words.Length == 1 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var k = 0; k < c.Words.Length; k++)
        {
            if (!string.Equals(words[at + k], c.Words[k], comparison))
            {
                return false;
            }
        }

        if (c.Words.Length == 1 &&
            ((at > 0 && Capitalised(words[at - 1])) || (at + 1 < words.Length && Capitalised(words[at + 1]))))
        {
            // A one-word name beside another capitalised word is part of a
            // longer name this server has never seen: "Horn" in "Efreeti War
            // Horn" is not the Horn.
            return false;
        }

        return true;
    }

    private static bool Capitalised(string word) => word.Length > 0 && char.IsUpper(word[0]);

    /// <summary>
    /// Splits on whitespace and trims the punctuation a sentence hangs on a
    /// word — the comma after "boots," the quote before 'Boots — but not the
    /// punctuation inside a name ("Spell: Holy Armor", "Journeyman's Boots",
    /// "Raw-Hide Mask"), because those are trimmed only from the ends and an
    /// apostrophe or hyphen mid-word stays.
    /// </summary>
    private static string[] Words(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var n = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var w = parts[i].Trim(TrimChars);
            if (w.Length > 0)
            {
                parts[n++] = w;
            }
        }

        if (n != parts.Length)
        {
            Array.Resize(ref parts, n);
        }

        return parts;
    }

    // Colon is deliberately absent: "Spell: Holy Armor" carries it inside the
    // name, and a chat line ending in a colon before an item is rare enough
    // to lose.
    private static readonly char[] TrimChars = ['\'', '"', ',', '.', '!', '?', ';', '(', ')', '[', ']', '<', '>'];

    private sealed record Candidate(string Name, string[] Words);
}
