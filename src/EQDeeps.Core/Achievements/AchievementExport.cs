using System.Text.RegularExpressions;

namespace EQDeeps.Core.Achievements;

/// <summary>
/// One progress line under an achievement — a counted objective ("Kobolds 570/5000") or one the
/// game has already finished. A finished component never carries a count (measured against the
/// whole reference file: not one keeps its numbers, and no open one lacks them — see
/// docs/domain/eq-client-files.md, "The achievements export"), so <see cref="Have"/> and
/// <see cref="Need"/> are only ever both present together, and only while <see cref="Complete"/>
/// is false.
/// </summary>
public sealed record AchievementComponent(string Text, bool Complete, bool Optional, int? Have, int? Need);

/// <summary>
/// One achievement and its components, in the order the export wrote them. A title is not a key —
/// four titles repeat in the reference file — so identity for anything built on top of this is
/// category + title + position (see <see cref="Slayer"/>).
/// </summary>
/// <param name="Category">
/// The most recent category header the export wrote before this achievement, verbatim. Empty for
/// the (unobserved but possible) achievement a hostile or truncated file lists before its first
/// header — there is nothing else it could honestly be.
/// </param>
public sealed record Achievement(string Category, string Title, bool Complete, IReadOnlyList<AchievementComponent> Components);

/// <summary>
/// The whole <c>/outputfile achievements</c> export, parsed. <see cref="SkippedLines"/> plays the
/// same role here that <c>Session.UnrecognizedLines</c> plays for the log (CLAUDE.md §4): a
/// player's own file is hostile input like any other, and "how many lines didn't parse" needs to
/// be a number the caller can check rather than an assumption.
/// </summary>
public sealed record AchievementExportFile(IReadOnlyList<Achievement> Achievements, int SkippedLines);

/// <summary>
/// Parses <c>&lt;Char&gt;_&lt;server&gt;-Achievements.txt</c>, written by <c>/outputfile
/// achievements</c> to the install root beside the inventory dump
/// (docs/domain/eq-client-files.md, "The achievements export"). It is the only place a kill count
/// toward an achievement exists outside the game (F35 / ADR-023 Decision 1) — the log never says
/// what race a corpse was, and the client's own achievement tables carry titles but no progress.
///
/// <para>The whole file is read, not just the Slayer categories: <see cref="Slayer"/>
/// is one projection over it, and Hunter/Exploration are the obvious next readers (ADR-023
/// Decision 2). Every method here is pure and never throws — this is the player's own file, read
/// from their install, and the log-format doc's hostile-input posture (CLAUDE.md §4) applies to it
/// exactly as it does to the log.</para>
/// </summary>
public static class AchievementExport
{
    public const string Command = "/outputfile achievements";

    // A line this long cannot be a real title, category or creature list — the longest genuine
    // line in the reference file is under 200 characters. Rejecting outright, rather than parsing
    // and truncating, keeps a corrupt file from costing more than a length check (ADR-023).
    private const int MaxLineLength = 1024;

    // 4 MB is two orders of magnitude past the reference file's 64 KB. Checked before any
    // splitting or allocation, per the log-format doc's rule that a hostile size must not be
    // allowed to cost parse time.
    private const int MaxTextLength = 4 * 1024 * 1024;

    private const string OptionalPrefix = "(Optional) ";

    private static readonly Regex CountColumn = new(@"^\d+/\d+$", RegexOptions.Compiled);

    /// <summary>Where the file lives under an install, for a character on a server — the same file-naming convention <c>InventoryDump</c> (F29) uses for its own dump.</summary>
    public static string PathFor(string installRoot, string character, string server) =>
        Path.Combine(installRoot, $"{character}_{server}-Achievements.txt");

    public static AchievementExportFile Parse(string text)
    {
        if (text.Length > MaxTextLength)
        {
            return new AchievementExportFile([], 1);
        }

        var achievements = new List<Achievement>();
        var components = new List<AchievementComponent>();
        var category = "";
        string? pendingTitle = null;
        var pendingComplete = false;
        var hasPending = false;
        var skipped = 0;

        void Flush()
        {
            if (hasPending)
            {
                achievements.Add(new Achievement(category, pendingTitle!, pendingComplete, components));
                components = new List<AchievementComponent>();
                hasPending = false;
            }
        }

        // Split on '\n' and drop a trailing '\r' per line: CRLF and LF must parse identically,
        // and this is the same trick InventoryDump/LootFilterFile use for the same reason.
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                // Not one of the four shapes, but not junk either — this is what a trailing
                // newline in an otherwise well-formed file produces, and Parse("") must return
                // SkippedLines == 0, not 1.
                continue;
            }

            if (line.Length > MaxLineLength)
            {
                skipped++;
                continue;
            }

            if (!line.Contains('\t'))
            {
                // Category: the header line, verbatim, with no tab-delimited structure at all.
                Flush();
                category = line;
                continue;
            }

            var parts = line.Split('\t');
            var state = parts[0];
            if (state != "I" && state != "C")
            {
                skipped++; // unknown state letter
                continue;
            }

            var complete = state == "C";

            if (parts.Length == 2)
            {
                // Achievement: state \t title.
                Flush();
                pendingTitle = parts[1];
                pendingComplete = complete;
                hasPending = true;
                continue;
            }

            if (parts.Length is 3 or 4 && parts[1].Length == 0)
            {
                // Component: state \t \t text [\t have/need].
                if (!hasPending)
                {
                    skipped++; // a component before any achievement line has no parent to attach to
                    continue;
                }

                int? have = null;
                int? need = null;
                if (parts.Length == 4)
                {
                    (have, need) = ParseCount(parts[3]);
                }

                components.Add(MakeComponent(parts[2], complete, have, need));
                continue;
            }

            skipped++; // a stray tab count: neither shape fits
        }

        Flush();
        return new AchievementExportFile(achievements, skipped);
    }

    // "Have/Need come from the next column only when it is exactly \d+/\d+; otherwise both are
    // null" (ADR-023) — a component with a trailing column that doesn't look like a count is still
    // a component, just one whose count could not be read, rather than a line to reject outright.
    private static (int? Have, int? Need) ParseCount(string column)
    {
        if (!CountColumn.IsMatch(column))
        {
            return (null, null);
        }

        var slash = column.IndexOf('/');
        if (int.TryParse(column.AsSpan(0, slash), out var have) &&
            int.TryParse(column.AsSpan(slash + 1), out var need))
        {
            return (have, need);
        }

        // A digit run too long for int (TryParse fails rather than throws) is the same "no
        // count" outcome as any other malformed column.
        return (null, null);
    }

    private static AchievementComponent MakeComponent(string text, bool complete, int? have, int? need)
    {
        var optional = text.StartsWith(OptionalPrefix, StringComparison.Ordinal);
        var cleaned = optional ? text[OptionalPrefix.Length..] : text;
        return new AchievementComponent(cleaned, complete, optional, have, need);
    }
}
