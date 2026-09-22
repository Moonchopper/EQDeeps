using System.Reflection;
using System.Text;

namespace EQDeeps.Core.Achievements;

/// <summary>
/// Joins an achievement's creature-type word ("Sporalis") to the reference's race label
/// ("Fungusman") — a hand-authored table, because neither side states a race id and there is
/// nothing to join them on (F35, ADR-023 Decision 3). See <c>slayer-races.tsv</c>'s own header for
/// the format, how the rows were authored, and what that authoring found — including that some
/// labels are the reference site's own mistakes (pandas filed under "Ulthork") which this table
/// maps to deliberately, because the atlas it feeds is grouped by what the site says.
/// </summary>
public sealed class SlayerRaces
{
    private readonly Dictionary<string, IReadOnlyList<string>> _byTerm;

    private SlayerRaces(Dictionary<string, IReadOnlyList<string>> byTerm)
    {
        _byTerm = byTerm;
    }

    /// <summary>The table shipped with the app.</summary>
    public static SlayerRaces Default { get; } = Load();

    /// <summary>
    /// How many terms the table knows — a whole-table sanity check for tests. <see cref="RacesFor"/>
    /// is the lookup surface a caller should use; this exists only because the dictionary behind it
    /// is otherwise opaque, and a duplicate term silently overwriting an earlier one is exactly the
    /// kind of authoring slip a raw row count cannot catch on its own.
    /// </summary>
    internal int Count => _byTerm.Count;

    /// <summary>
    /// Reads the TSV form: <c>term\trace[|race...][\twhy]</c>. Blank lines and <c>#</c> comments are
    /// skipped; a row with no term or no race is skipped rather than thrown — this table is
    /// hand-authored and can be hand-broken, and CLAUDE.md §4's "never throw on malformed input"
    /// rule applies to our own data files exactly as it does to the log.
    /// </summary>
    public static SlayerRaces Parse(string text)
    {
        var byTerm = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in text.Split('\n'))
        {
            // Trimming '\r' only, never the whole line: a leading tab is a real, empty first
            // column (a row with no term), and trimming it away would shift every cell over and
            // read the race as the term instead of skipping the row. The blank/comment check below
            // looks at a separately-trimmed copy so it is not fooled by incidental indentation.
            var line = raw.TrimEnd('\r');
            var trimmedForCheck = line.TrimStart();
            if (trimmedForCheck.Length == 0 || trimmedForCheck[0] == '#')
            {
                continue;
            }

            var cells = line.Split('\t');
            if (cells.Length < 2)
            {
                continue;
            }

            var term = cells[0].Trim();
            var races = cells[1].Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (term.Length == 0 || races.Length == 0)
            {
                continue;
            }

            byTerm[term] = races;
        }

        return new SlayerRaces(byTerm);
    }

    /// <summary>The races the reference labels this term with, matched exactly and case-insensitively, or empty when the term has no known location in this game.</summary>
    public IReadOnlyList<string> RacesFor(string term) =>
        _byTerm.TryGetValue(term, out var races) ? races : [];

    private static SlayerRaces Load()
    {
        var assembly = typeof(SlayerRaces).GetTypeInfo().Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("slayer-races.tsv", StringComparison.Ordinal));

        if (name is null)
        {
            return new SlayerRaces(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
        }

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }
}
