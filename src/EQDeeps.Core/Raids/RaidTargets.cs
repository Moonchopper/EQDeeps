using System.Reflection;
using System.Text;

namespace EQDeeps.Core.Raids;

/// <summary>
/// One named mob the Raid targets view lists (F31, ADR-022 Decision 4).
/// </summary>
/// <param name="Name">
/// As the log's slain line prints it — the log is the authority for matching,
/// because that is the string a kill arrives as.
/// </param>
/// <param name="Aliases">
/// Other spellings of the same mob — typically the reference site's, where it
/// differs from the log's own (it drops the hyphen from Cazic-Thule and the
/// title from Innoruuk) — so the door to the Bestiary still opens. Empty, not
/// containing an empty string, when the row names none.
/// </param>
/// <param name="Zone">
/// Where it lives, as <c>zones.tsv</c> names the place — for the heading and
/// the door to the map. Where a kill happened comes from the death record's
/// own zone dimension, never from this column.
/// </param>
/// <param name="Group">The heading it is listed under.</param>
public sealed record RaidTarget(string Name, IReadOnlyList<string> Aliases, string Zone, string Group);

/// <summary>
/// The hand-authored roster of raid targets, embedded the way <c>zones.tsv</c>
/// is (<see cref="Maps.ZoneTable"/>): a short list about content the owner
/// plays is worth a one-line diff a non-programmer can read, not a migration.
///
/// <para><b>Matching is not this type's job.</b> A logged name meets a
/// roster name under the article-stripped, case-folded key
/// <see cref="Reference.NpcIndex.Normalize"/> plus
/// <see cref="StringComparer.OrdinalIgnoreCase"/> already give — the same key
/// the Bestiary uses (ADR-020) and <c>mobKey</c> mirrors in the UI. This type
/// only reads the file; the join happens where the query rows are (the
/// server has no raid-specific aggregation to do it in, and the client
/// already has both halves — see the F31-3 brief's Recon §4b).</para>
/// </summary>
public sealed class RaidTargets
{
    private RaidTargets(IReadOnlyList<RaidTarget> targets) => Targets = targets;

    /// <summary>Every target, in file order — the order the page groups and lists them in.</summary>
    public IReadOnlyList<RaidTarget> Targets { get; }

    /// <summary>The roster shipped with the app.</summary>
    public static RaidTargets Default { get; } = Load();

    /// <summary>
    /// Reads the TSV form: <c>name\taliases\tzone\tgroup</c>, aliases
    /// <c>|</c>-separated. Blank lines and <c>#</c> comments are skipped; so
    /// is a malformed row, on the same tolerance principle as the log parser
    /// and <see cref="Maps.ZoneTable.Parse"/> — never thrown.
    ///
    /// <para><b>The header row.</b> Recognised by its first cell being
    /// exactly <c>name</c>, not by its position — a data row can never
    /// legitimately start that way, and checking the content rather than
    /// "skip row one" survives a stray blank or comment line above it.</para>
    ///
    /// <para><b>Empty aliases.</b> Almost every row's aliases cell is empty
    /// (two consecutive tabs). Splitting it with
    /// <see cref="StringSplitOptions.RemoveEmptyEntries"/> yields an empty
    /// list; splitting it plainly would yield a one-element list holding the
    /// empty string, which matches nothing but is a trap for whatever builds
    /// the key lookup from it.</para>
    /// </summary>
    public static RaidTargets Parse(string tsv)
    {
        var targets = new List<RaidTarget>();

        foreach (var line in tsv.Split('\n'))
        {
            var row = line.Trim();
            if (row.Length == 0 || row[0] == '#')
            {
                continue;
            }

            var cells = row.Split('\t');
            if (cells.Length < 4)
            {
                continue;
            }

            var name = cells[0].Trim();
            if (name == "name")
            {
                continue; // the header row, recognised by content
            }

            var zone = cells[2].Trim();
            var group = cells[3].Trim();
            if (name.Length == 0 || zone.Length == 0 || group.Length == 0)
            {
                continue;
            }

            var aliases = cells[1].Trim().Split(
                '|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            targets.Add(new RaidTarget(name, aliases, zone, group));
        }

        return new RaidTargets(targets);
    }

    private static RaidTargets Load()
    {
        var assembly = typeof(RaidTargets).GetTypeInfo().Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("raid-targets.tsv", StringComparison.Ordinal));

        if (name is null)
        {
            return new RaidTargets(Array.Empty<RaidTarget>());
        }

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }
}
