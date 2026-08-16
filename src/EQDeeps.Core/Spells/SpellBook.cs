namespace EQDeeps.Core.Spells;

/// <summary>
/// What an emote line resolved to. <see cref="Spell"/> is named only when the
/// message belongs to exactly one spell; when several share it — and many do,
/// every rank of a heal saying "Your wounds begin to heal." — the name is null
/// and <see cref="Candidates"/> says how many it could have been. Something
/// landed, and we know when and on whom; pretending to know which rank would
/// be a worse answer than admitting the message does not carry it.
/// </summary>
public sealed record SpellMatch(string? Spell, int Candidates)
{
    public bool IsAmbiguous => Spell is null && Candidates > 1;
}

/// <summary>
/// The spells the player's own game client knows, read from the two files it
/// ships beside the log (see <c>docs/domain/eq-client-files.md</c>):
///
/// <list type="bullet">
/// <item><c>spells_us.txt</c> — <c>id^name^…</c>, 73,963 rows and 173 columns
/// of which only the first two are read here. The rest (durations, class
/// levels, resists) are unlabelled and would need to be identified by
/// experiment; that is a separate piece of work with its own evidence, not a
/// guess folded into this one.</item>
/// <item><c>spells_us_str.txt</c> — headed
/// <c>#SPELLINDEX^CASTERMETXT^CASTEROTHERTXT^CASTEDMETXT^CASTEDOTHERTXT^SPELLGONE^</c>,
/// which is the map from a line the log actually contains back to a spell.</item>
/// </list>
///
/// <para><b>Why this exists.</b> A cast names its spell, but a buff landing on
/// you does not: the client prints per-spell emote text ("A burst of strength
/// surges through your body."), and so does the fade. Three doc comments in
/// this codebase have said the spell database would resolve those later; this
/// is later. In the owner's log 30,829 lands-on lines and 11,716 fades were
/// going past as unrecognized.</para>
///
/// <para><b>Read, never bundled.</b> The files belong to the player's install,
/// exactly like the maps (F27) and the loot-filter file (F29). Nothing is
/// copied into the repo, so there is no licence question and no stale copy.
/// An install that is absent leaves an empty book, and every lookup politely
/// answers "no".</para>
/// </summary>
public sealed class SpellBook
{
    /// <summary>A book that knows nothing — what a session gets when the log has no install beside it.</summary>
    public static readonly SpellBook Empty = new([], [], [], []);

    /// <summary>Spell name → the duration pair from columns 107/108 (see <see cref="SpellDuration"/>).</summary>
    private readonly Dictionary<string, (int Formula, int Cap)> _durations;

    private readonly Dictionary<string, SpellMatch> _landsOnYou;
    private readonly Dictionary<string, SpellMatch> _landsOnOther;
    private readonly Dictionary<string, SpellMatch> _fades;

    private SpellBook(
        Dictionary<string, SpellMatch> landsOnYou,
        Dictionary<string, SpellMatch> landsOnOther,
        Dictionary<string, SpellMatch> fades,
        Dictionary<string, (int Formula, int Cap)> durations)
    {
        _landsOnYou = landsOnYou;
        _landsOnOther = landsOnOther;
        _fades = fades;
        _durations = durations;
    }

    /// <summary>
    /// How long this spell lasts when cast by someone of this level, or null
    /// when the spell is unknown, instant, or does not expire on its own.
    /// </summary>
    public TimeSpan? DurationOf(string spell, int casterLevel) =>
        _durations.TryGetValue(spell, out var d)
            ? SpellDuration.Duration(d.Formula, d.Cap, casterLevel)
            : null;

    public int DurationCount => _durations.Count;

    public bool IsEmpty => _landsOnYou.Count == 0 && _landsOnOther.Count == 0 && _fades.Count == 0;

    public int LandsOnYouCount => _landsOnYou.Count;

    public int LandsOnOtherCount => _landsOnOther.Count;

    public int FadeCount => _fades.Count;

    /// <summary>
    /// The longest "lands on someone else" fragment this line ends with, and
    /// the name it was appended to. The client writes these as a name plus a
    /// fragment — "Soandso's blood ignites.", "Soandso shimmers." — with no
    /// separator to split on, so the fragment has to be recognised from the
    /// end. Longest wins, because "'s blood ignites." and "ignites." can both
    /// be spells and the longer is the more specific reading.
    /// </summary>
    public bool TryLandsOnOther(string line, out string target, out SpellMatch match)
    {
        target = string.Empty;
        match = new SpellMatch(null, 0);
        if (_landsOnOther.Count == 0 || line.Length < 2)
        {
            return false;
        }

        // Fragments begin either at a word boundary (" shimmers.") or at the
        // possessive ("'s blood ignites."), so every space and apostrophe is a
        // candidate split. Walking from the left finds the longest tail first.
        for (var i = 0; i < line.Length - 1; i++)
        {
            var c = line[i];
            if (c is not (' ' or '\''))
            {
                continue;
            }

            // " shimmers." — the fragment excludes the space that precedes it.
            var start = c == ' ' ? i + 1 : i;
            if (start >= line.Length)
            {
                break;
            }

            if (_landsOnOther.TryGetValue(line[start..], out var found))
            {
                var candidate = line[..i].TrimEnd();
                if (!IsPlausibleTarget(candidate))
                {
                    // "The barking fades." would otherwise split into a target
                    // of "The". A bare article is nobody, so keep walking —
                    // a longer fragment further along may still be the answer.
                    continue;
                }

                target = candidate;
                match = found;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a split left something that could be a name. Real targets are
    /// a player's single word or a mob's "a/an/the &lt;something&gt;"; an
    /// article on its own is the split landing in the middle of a sentence.
    /// </summary>
    private static bool IsPlausibleTarget(string candidate) =>
        candidate.Length > 0
        && !candidate.Equals("the", StringComparison.OrdinalIgnoreCase)
        && !candidate.Equals("a", StringComparison.OrdinalIgnoreCase)
        && !candidate.Equals("an", StringComparison.OrdinalIgnoreCase);

    /// <summary>The spell whose "lands on you" text this line is, if it is one.</summary>
    public bool TryLandsOnYou(string line, out SpellMatch match) => _landsOnYou.TryGetValue(line, out match!);

    /// <summary>The spell whose fade text this line is, if it is one.</summary>
    public bool TryFade(string line, out SpellMatch match) => _fades.TryGetValue(line, out match!);

    /// <summary>
    /// Builds a book from the two files' contents. Rows that do not parse are
    /// skipped: these are files on a player's disk, and a client patch may
    /// widen them at any time.
    /// </summary>
    public static SpellBook Build(string spellsUs, string spellsUsStr)
    {
        var (names, durations) = ParseSpells(spellsUs);
        if (names.Count == 0)
        {
            return Empty;
        }

        var landsOnYou = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var landsOnOther = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var fades = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var line in Lines(spellsUsStr))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split('^');
            // id ^ caster-me ^ caster-other ^ landed-me ^ landed-other ^ gone
            if (parts.Length < 6 || !names.TryGetValue(parts[0], out var name))
            {
                continue;
            }

            Add(landsOnYou, parts[3], name);
            Add(landsOnOther, parts[4], name);
            Add(fades, parts[5], name);
        }

        return new SpellBook(Collapse(landsOnYou), Collapse(landsOnOther), Collapse(fades), durations);
    }

    /// <summary>Duration formula, and its cap: columns 107 and 108 (see <see cref="SpellDuration"/> for how they were identified).</summary>
    private const int FormulaColumn = 107;
    private const int CapColumn = 108;

    /// <summary>
    /// id → name, and name → duration pair. Only four of the file's 173
    /// columns are read; a row too short to hold them still contributes its
    /// name, because the emote grammars need only that.
    /// </summary>
    private static (Dictionary<string, string> Names, Dictionary<string, (int, int)> Durations) ParseSpells(string spellsUs)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var durations = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        foreach (var line in Lines(spellsUs))
        {
            var first = line.IndexOf('^');
            if (first <= 0)
            {
                continue;
            }

            var parts = line.Split('^');
            if (parts.Length < 2 || parts[1].Length == 0)
            {
                continue;
            }

            var name = parts[1];
            names[parts[0]] = name;
            if (parts.Length > CapColumn &&
                int.TryParse(parts[FormulaColumn], out var formula) &&
                int.TryParse(parts[CapColumn], out var cap) &&
                formula > 0)
            {
                // First spelling wins: ranks share a name only rarely, and the
                // first row is the one the emote maps were built against.
                durations.TryAdd(name, (formula, cap));
            }
        }

        return (names, durations);
    }

    private static void Add(Dictionary<string, List<string>> map, string message, string spell)
    {
        var text = message.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (!map.TryGetValue(text, out var list))
        {
            list = [];
            map[text] = list;
        }

        // Ranks of one spell repeat their emote; count each spell once so
        // "shared by 39" means 39 spells, not 39 rows.
        if (!list.Contains(spell))
        {
            list.Add(spell);
        }
    }

    private static Dictionary<string, SpellMatch> Collapse(Dictionary<string, List<string>> map)
    {
        var result = new Dictionary<string, SpellMatch>(map.Count, StringComparer.Ordinal);
        foreach (var (message, spells) in map)
        {
            result[message] = new SpellMatch(spells.Count == 1 ? spells[0] : null, spells.Count);
        }

        return result;
    }

    private static IEnumerable<string> Lines(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            yield return raw.TrimEnd('\r');
        }
    }
}
