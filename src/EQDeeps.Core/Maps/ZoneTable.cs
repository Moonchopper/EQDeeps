using System.Collections.Frozen;
using System.Reflection;
using System.Text;
using EQDeeps.Core.Parsing;

namespace EQDeeps.Core.Maps;

/// <summary>How a row of the zone table was arrived at, and therefore how much to trust it.</summary>
public enum ZoneNameSource
{
    /// <summary>
    /// The short name and the display name agree once punctuation and a leading
    /// "The" are removed (<c>arxmentis</c> ↔ "Arx Mentis"). Mechanical, and as
    /// close to certain as this table gets.
    /// </summary>
    Name,

    /// <summary>
    /// Deduced from the connection graph: at least two already-known
    /// neighbours name this zone and it names them back. See
    /// <see cref="ZoneTable"/> for the argument.
    /// </summary>
    Graph,

    /// <summary>
    /// Written down by hand. The display name is still checked against the
    /// client's own name table, so the string is real — but the *pairing* is
    /// the one thing in this file resting on somebody's word.
    /// </summary>
    Curated,
}

/// <summary>One zone's short name, the name the log says, and where the pairing came from.</summary>
public sealed record ZoneEntry(string ShortName, string DisplayName, ZoneNameSource Source);

/// <summary>
/// Joins the name the log speaks ("The Estate of Unrest") to the name the map
/// files are stored under (<c>unrest</c>).
///
/// <para><b>Why this has to exist at all.</b> Nothing in an EverQuest install
/// makes this join. <c>Resources/ZoneNames.txt</c> lists display names against
/// zone ids; the <c>maps/</c> folder is named by short name; no file carries
/// both. It is not an oversight — the client is told its zone's short name by
/// the server on zone-in, so it never needs a table. The log, which is all
/// EQDeeps has, records only the display name.</para>
///
/// <para><b>How the shipped rows were derived.</b> Short names are historical
/// abbreviations, not contractions of the display name, so string matching
/// alone resolves only 108 of the 581 zones that have maps. The rest came from
/// the maps themselves: a map's <c>to_&lt;Zone&gt;</c> labels name its
/// neighbours in display-name space, so once some zones are known, an unknown
/// one is pinned by the neighbours that point at it — and confirmed when it
/// points back. Requiring two independent neighbours and a reciprocated edge
/// added 31 more, every one of them reciprocated. A single neighbour is not
/// evidence: it merely names whatever is left over, which is how an early pass
/// decided <c>oldblackburrow</c> was The Void.</para>
///
/// <para>The remaining 84 rows are hand-written, and every display name in the
/// file — derived or curated — is checked verbatim against the client's own
/// name table by <c>ZoneTableTests</c>. That catches an invented name but not
/// an invented pairing, which is why <see cref="ZoneNameSource"/> is carried
/// through to the UI rather than smoothed away.</para>
///
/// <para><b>The table is deliberately incomplete.</b> 223 of 581 short names,
/// covering 128 of the 133 zones a stock client ships a map for. An unknown
/// zone is not an error — it resolves to no map and the user picks one, which
/// is also the escape hatch for a pairing this file gets wrong.</para>
/// </summary>
public sealed class ZoneTable
{
    private readonly FrozenDictionary<string, IReadOnlyList<string>> _byDisplay;
    private readonly FrozenDictionary<string, ZoneEntry> _byShortName;

    private ZoneTable(IReadOnlyList<ZoneEntry> entries)
    {
        Entries = entries;

        _byShortName = entries
            .GroupBy(e => e.ShortName, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Many-to-one is normal, not a defect: a zone that was revamped keeps
        // its old map alongside the new one (freportw and freeportwest are both
        // "West Freeport"), and the player is the only one who knows which they
        // are looking at. Both are offered rather than one being guessed.
        _byDisplay = entries
            .GroupBy(e => Normalize(e.DisplayName), StringComparer.Ordinal)
            .ToFrozenDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(e => e.ShortName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.Ordinal);
    }

    public IReadOnlyList<ZoneEntry> Entries { get; }

    /// <summary>The table shipped with the app.</summary>
    public static ZoneTable Default { get; } = Load();

    /// <summary>
    /// Every map short name that could be the zone the log just named, best
    /// first, or empty when the zone is not in the table.
    ///
    /// <para>The argument may carry an instance suffix — "The Estate of Unrest
    /// 4 (Refined)" — because that is what the log line says. An instance is
    /// the same geometry as its open-world zone, so the suffix is stripped
    /// rather than looked up.</para>
    /// </summary>
    public IReadOnlyList<string> MapsFor(string zoneName)
    {
        if (string.IsNullOrWhiteSpace(zoneName))
        {
            return Array.Empty<string>();
        }

        var key = Normalize(InstanceZone.Parse(zoneName).BaseName);
        return _byDisplay.TryGetValue(key, out var shorts) ? shorts : Array.Empty<string>();
    }

    /// <summary>The display name for a map short name, or null if unknown.</summary>
    public string? DisplayFor(string shortName) =>
        _byShortName.TryGetValue(shortName, out var entry) ? entry.DisplayName : null;

    public ZoneEntry? EntryFor(string shortName) =>
        _byShortName.TryGetValue(shortName, out var entry) ? entry : null;

    /// <summary>
    /// Reads the TSV form: <c>shortname\tdisplay\tsource</c>. Blank lines and
    /// <c>#</c> comments are skipped; a row that does not parse is skipped
    /// rather than thrown, on the same principle as the log parser.
    /// </summary>
    public static ZoneTable Parse(string tsv)
    {
        var entries = new List<ZoneEntry>();

        foreach (var line in tsv.Split('\n'))
        {
            var row = line.AsSpan().Trim();
            if (row.IsEmpty || row[0] == '#')
            {
                continue;
            }

            var first = row.IndexOf('\t');
            if (first <= 0)
            {
                continue;
            }

            var rest = row[(first + 1)..];
            var second = rest.IndexOf('\t');
            var display = (second < 0 ? rest : rest[..second]).Trim();
            if (display.IsEmpty)
            {
                continue;
            }

            var source = second < 0
                ? ZoneNameSource.Curated
                : rest[(second + 1)..].Trim() switch
                {
                    "name" => ZoneNameSource.Name,
                    "graph" => ZoneNameSource.Graph,
                    _ => ZoneNameSource.Curated,
                };

            entries.Add(new ZoneEntry(row[..first].Trim().ToString(), display.ToString(), source));
        }

        return new ZoneTable(entries);
    }

    private static ZoneTable Load()
    {
        var assembly = typeof(ZoneTable).GetTypeInfo().Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("zones.tsv", StringComparison.Ordinal));

        if (name is null)
        {
            return new ZoneTable(Array.Empty<ZoneEntry>());
        }

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// The comparison key for a zone name. Mapmakers write apostrophes as
    /// backticks, drop the leading "The", and punctuate as the mood takes them,
    /// so all three are removed before comparing. "Bazaar" and "The Bazaar" are
    /// the same place and must land on the same key.
    /// </summary>
    internal static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);

        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        var s = sb.ToString();
        return s.StartsWith("the", StringComparison.Ordinal) && s.Length > 3 ? s[3..] : s;
    }
}
