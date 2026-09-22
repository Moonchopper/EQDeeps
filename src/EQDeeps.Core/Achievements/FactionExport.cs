namespace EQDeeps.Core.Achievements;

/// <summary>One row of the faction export: the client's own id and spelling for a faction, and the character's standing with it.</summary>
public sealed record FactionStanding(int Id, string Name, int Standing);

/// <summary>
/// The whole <c>/outputfile faction</c> export, parsed. <see cref="SkippedLines"/> plays the same
/// role <c>AchievementExportFile</c>'s does (CLAUDE.md §4): a player's own file is hostile input
/// like any other, and "how many rows didn't parse" needs to be a number the caller can check
/// rather than an assumption.
/// </summary>
public sealed record FactionExportFile(IReadOnlyList<FactionStanding> Factions, int SkippedLines);

/// <summary>
/// Parses <c>&lt;Char&gt;_&lt;server&gt;-&lt;CLASS&gt;-Factions.txt</c>, written by <c>/outputfile
/// faction</c> to the install root (docs/domain/eq-client-files.md, "The faction export"). It is
/// the second half of ADR-023 Decision 5: the achievements export says whether a faction loss can
/// still cost an unlock, and this file says where a loss would actually leave the character's
/// standing — the two turned out to answer different questions on the owner's own files, six of
/// the forty protected factions among them (a completed "maximum faction" unlock sitting at 0).
///
/// <para>This file is <b>per class loadout</b> — Legends gives one character three
/// (eq-legends-loadouts.md) — so it is the first per-loadout file the app reads. A parser here
/// cannot know which of several candidate files on disk is newest; that needs the filesystem, which
/// this pure parser does not touch. <see cref="SearchPattern"/> and <see cref="ClassOf"/> are what
/// a caller globs a directory and labels the results with; picking the most recently written one is
/// the caller's job.</para>
///
/// <para><c>PointsToMax</c> is ignored entirely: it is always <c>2000 - StandingValue</c> (186 of
/// 186 measured), so carrying it would only be a second copy of the same fact to drift out of sync.
/// Same hostile-input posture and caps as <c>AchievementExport</c>, for the same reason — this is
/// the player's own file, read from their install, and a corrupt or truncated one must cost no more
/// than a length check.</para>
/// </summary>
public static class FactionExport
{
    public const string Command = "/outputfile faction";

    // A line this long cannot be a real faction row — the longest genuine row in the reference
    // file is well under 100 characters. Same bound as AchievementExport, and the same reasoning:
    // reject outright rather than parse-and-truncate, so a corrupt file costs one length check.
    private const int MaxLineLength = 1024;

    // Two orders of magnitude past the reference file's 5.6 KB, checked before any splitting.
    private const int MaxTextLength = 4 * 1024 * 1024;

    private const string HeaderRow = "ID\tName\tStandingValue\tPointsToMax";

    /// <summary>Every class loadout's faction export for a character on a server — glob this and take the newest file.</summary>
    public static string SearchPattern(string character, string server) =>
        $"{character}_{server}-*Factions.txt";

    /// <summary>
    /// The class segment between the character/server prefix and the trailing "-Factions.txt", or
    /// null when the name carries none — a directory glob can turn up other files, and a caller
    /// needs to tell a real class loadout export from something that merely matches the pattern.
    /// </summary>
    public static string? ClassOf(string fileName, string character, string server)
    {
        var prefix = $"{character}_{server}-";
        const string suffix = "Factions.txt";
        if (fileName.Length < prefix.Length + suffix.Length ||
            !fileName.StartsWith(prefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        // What's left between the prefix and the suffix is "SHD-" for a real class loadout export
        // and "" for the classless shape ("Moonchopper_qeynos-Factions.txt") — the latter is not a
        // shape this file ever actually takes, but a caller globbing a directory does not know
        // that, so it must read back null rather than an empty class name.
        var middle = fileName[prefix.Length..^suffix.Length];
        return middle.Length > 1 && middle[^1] == '-' ? middle[..^1] : null;
    }

    public static FactionExportFile Parse(string text)
    {
        if (text.Length > MaxTextLength)
        {
            return new FactionExportFile([], 1);
        }

        var factions = new List<FactionStanding>();
        var skipped = 0;

        // Split on '\n' and drop a trailing '\r' per line: CRLF and LF must parse identically, the
        // same trick AchievementExport/InventoryDump/LootFilterFile use for the same reason.
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line == HeaderRow)
            {
                continue; // recognised and skipped, uncounted
            }

            if (line.Length > MaxLineLength)
            {
                skipped++;
                continue;
            }

            var cells = line.Split('\t');
            if (cells.Length < 3 ||
                !int.TryParse(cells[0], out var id) ||
                cells[1].Length == 0 ||
                !int.TryParse(cells[2], out var standing))
            {
                skipped++;
                continue;
            }

            factions.Add(new FactionStanding(id, cells[1], standing));
        }

        return new FactionExportFile(factions, skipped);
    }
}
