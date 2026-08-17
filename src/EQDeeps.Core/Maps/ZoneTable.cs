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

/// <summary>
/// One zone's short name, the name the log says, and where the pairing came
/// from — plus the earliest expansion the place exists in, when the table can
/// say (<see cref="ZoneEras"/>).
/// </summary>
/// <param name="Era">
/// A <see cref="ZoneEra.Id"/>, or null when unknown. Null is a first-class
/// answer, not a gap: it means "shown under every era filter".
/// </param>
/// <param name="Ids">
/// Every zone id the client's own name table gives this display name,
/// ascending; empty when the table has none. Several is normal — a revamp
/// keeps the name ("The Ocean of Tears" is 69, 409 and 569) — and so is two
/// short names sharing the same ids, since <c>freportw</c> and
/// <c>freeportwest</c> are two drawings of one name. The ids are the client's
/// numbers, read off <c>ZoneNames.txt</c> offline by
/// <c>scripts/derive-zone-eras.mjs</c>; the reference layer (ADR-020) uses
/// them as addresses, and checks what it finds at one against content.
/// </param>
public sealed record ZoneEntry(
    string ShortName,
    string DisplayName,
    ZoneNameSource Source,
    string? Era = null,
    ZoneEraSource? EraSource = null,
    IReadOnlyList<int>? Ids = null)
{
    public IReadOnlyList<int> Ids { get; init; } = Ids ?? Array.Empty<int>();
}

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
/// <para>The remaining 130 rows are hand-written, and every display name in the
/// file — derived or curated — is checked verbatim against the client's own
/// name table by <c>ZoneTableTests</c>. That catches an invented name but not
/// an invented pairing, which is why <see cref="ZoneNameSource"/> is carried
/// through to the UI rather than smoothed away.</para>
///
/// <para><b>The table is deliberately incomplete.</b> 268 of 581 short names,
/// covering 128 of the 133 zones a stock client ships a map for. An unknown
/// zone is not an error — it resolves to no map and the user picks one, which
/// is also the escape hatch for a pairing this file gets wrong.</para>
///
/// <para><b>Eras.</b> Two further columns say the earliest expansion each place
/// exists in and how that was decided. They are derived offline from the zone
/// ids in the client's own name table by <c>scripts/derive-zone-eras.mjs</c>
/// and checked in as data, so the app never reads the player's install for
/// them; see <see cref="ZoneEras"/> for what an era means and the map format
/// doc §5.3 for the id bands and their evidence.</para>
/// </summary>
public sealed class ZoneTable
{
    private readonly FrozenDictionary<string, IReadOnlyList<string>> _byDisplay;
    private readonly FrozenDictionary<string, ZoneEntry> _byShortName;
    private readonly FrozenDictionary<int, IReadOnlyList<ZoneEntry>> _byId;

    private ZoneTable(IReadOnlyList<ZoneEntry> entries)
    {
        Entries = entries;

        _byShortName = entries
            .GroupBy(e => e.ShortName, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // An id names a place, and a place may have several drawings — so an
        // id maps to every entry that carries it, first-listed first, and the
        // caller decides which map to open the way the Map view already does.
        _byId = entries
            .SelectMany(e => e.Ids.Select(id => (id, e)))
            .GroupBy(x => x.id)
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<ZoneEntry>)g.Select(x => x.e).ToArray());

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
    /// The earliest expansion a map's zone exists in, or null when the table
    /// cannot say — which includes a short name it does not know at all.
    /// </summary>
    public string? EraFor(string shortName) =>
        _byShortName.TryGetValue(shortName, out var entry) ? entry.Era : null;

    /// <summary>
    /// The entries carrying a client zone id — the place that id names, in
    /// each of its drawings — or empty when no row does.
    /// </summary>
    public IReadOnlyList<ZoneEntry> ZonesForId(int id) =>
        _byId.TryGetValue(id, out var entries) ? entries : Array.Empty<ZoneEntry>();

    /// <summary>
    /// Reads the TSV form:
    /// <c>shortname\tdisplay\tsource[\tera\terasource[\tids]]</c>. Blank lines
    /// and <c>#</c> comments are skipped; a row that does not parse is skipped
    /// rather than thrown, on the same principle as the log parser. An era code
    /// this build does not recognise is read as no era — shown, not hidden — for
    /// the same reason, and an id that is not a number is dropped from the list
    /// rather than sinking the row.
    /// </summary>
    public static ZoneTable Parse(string tsv)
    {
        var entries = new List<ZoneEntry>();

        foreach (var line in tsv.Split('\n'))
        {
            var row = line.Trim();
            if (row.Length == 0 || row[0] == '#')
            {
                continue;
            }

            var cells = row.Split('\t');
            if (cells.Length < 2)
            {
                continue;
            }

            var shortName = cells[0].Trim();
            var display = cells[1].Trim();
            if (shortName.Length == 0 || display.Length == 0)
            {
                continue;
            }

            var source = cells.Length < 3
                ? ZoneNameSource.Curated
                : cells[2].Trim() switch
                {
                    "name" => ZoneNameSource.Name,
                    "graph" => ZoneNameSource.Graph,
                    _ => ZoneNameSource.Curated,
                };

            var era = cells.Length > 3 ? ZoneEras.Find(cells[3].Trim())?.Id : null;

            // A source only means anything beside an era. A row that names one
            // without the other is treated as saying nothing about eras.
            var eraSource = era is null
                ? (ZoneEraSource?)null
                : cells.Length > 4 && cells[4].Trim() == "curated"
                    ? ZoneEraSource.Curated
                    : ZoneEraSource.Id;

            var ids = cells.Length > 5
                ? cells[5].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
                    .Where(id => id is > 0)
                    .Select(id => id!.Value)
                    .Distinct()
                    .ToArray()
                : Array.Empty<int>();

            entries.Add(new ZoneEntry(shortName, display, source, era, eraSource, ids));
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
    public static string Normalize(string value)
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
