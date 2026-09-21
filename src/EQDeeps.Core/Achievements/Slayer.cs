using System.Text.RegularExpressions;

namespace EQDeeps.Core.Achievements;

/// <summary>A kill achievement's own component, carried through unchanged from the export.</summary>
public sealed record SlayerKillComponent(string Text, bool Complete, bool Optional, int? Have, int? Need);

/// <summary>
/// A Slayer achievement that counts kills directly (<c>Slayer: Conquest</c>, <c>Special</c> or
/// <c>Skill</c>), with the progress math done once so every reader agrees on it.
/// </summary>
/// <param name="Key">
/// <c>"&lt;Category&gt;/&lt;Title&gt;"</c>, with <c>"#n"</c> appended for the n-th (n ≥ 2)
/// achievement sharing a category and title — a title alone is not unique (four repeat in the
/// reference file).
/// </param>
/// <param name="Fraction">
/// 0..1, the mean of each non-optional component's own progress, except an achievement the export
/// marks complete is always 1 regardless of what its components say — the export's own verdict
/// outranks a recomputation from parts that may no longer agree with it.
/// </param>
/// <param name="Remaining">
/// Kills still needed across open, non-optional, counted components, or null when there are none
/// to sum (finished, or every component is optional or uncounted).
/// </param>
public sealed record SlayerKillAchievement(string Key, string Tier, string Title, bool Complete,
    IReadOnlyList<SlayerKillComponent> Components, double Fraction, int? Remaining);

/// <summary>
/// One reference inside a meta-achievement to another achievement, by title. The reference line's
/// own <see cref="Complete"/> is kept as written — never recomputed from
/// <see cref="TargetKey"/>'s achievement — because that is what the export itself records (ADR-023
/// Decision 2).
/// </summary>
/// <param name="TargetKey">
/// The <see cref="SlayerKillAchievement.Key"/> or <see cref="SlayerMetaAchievement.Key"/> the
/// quoted title resolves to, or null when nothing matches. Measured on the reference file: 93 of
/// 179 references match a title verbatim, 27 more only after normalising case and trailing
/// punctuation, and 59 match nothing — mostly achievements for creatures this game does not have
/// yet. An unresolved reference is still a row; resolving it is for linking only.
/// </param>
public sealed record SlayerMetaComponent(string Title, bool Complete, bool Optional, string? TargetKey);

/// <summary>
/// A <c>Slayer: General</c> achievement whose components each point at another achievement by
/// title ("Complete the achievement …") rather than counting a kill directly.
/// </summary>
public sealed record SlayerMetaAchievement(string Key, string Title, bool Complete,
    IReadOnlyList<SlayerMetaComponent> Components, int RequiredDone, int RequiredTotal);

/// <summary>The Slayer achievements out of one export, split into the two shapes the category holds.</summary>
public sealed record SlayerProgress(IReadOnlyList<SlayerMetaAchievement> Meta, IReadOnlyList<SlayerKillAchievement> Kills);

/// <summary>
/// Projects the Slayer categories out of a parsed <see cref="AchievementExportFile"/> (F35 /
/// ADR-023 Decision 2 — "Slayer" is whatever the file's own category says, nothing more). No
/// creature-type splitting happens here: a kill component's <see cref="SlayerKillComponent.Text"/>
/// is carried verbatim, because turning a prose creature list ("Alligators, Basilisks, and
/// Crocodiles.") into races is slice 2's join table, not this slice.
/// </summary>
public static class Slayer
{
    private const string CategoryPrefix = "Slayer:";

    // Anchored on both ends: a meta reference is nothing but this sentence, quoting the target
    // achievement's title as the export itself wrote it.
    private static readonly Regex MetaReference = new(
        "^Complete the achievement \"(?<title>.*)\"$", RegexOptions.Compiled);

    // Trimmed off both operands before a title comparison — the reference file's near-misses are
    // all case and trailing punctuation ("Catnipped in the bud." vs "Catnipped In the Bud").
    private static readonly char[] TitleTrim = ['.', '!', '?', ' ', '\t', '\r', '\n'];

    public static SlayerProgress From(AchievementExportFile file)
    {
        var inScope = file.Achievements
            .Where(a => a.Category.StartsWith(CategoryPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Pass 1: assign every in-scope achievement its key and index it by normalised title, so a
        // meta reference can resolve to an achievement regardless of which pass built it (a meta
        // achievement may reference another meta achievement, not only a kill one).
        var keys = new string[inScope.Count];
        var keyCounts = new Dictionary<(string Category, string Title), int>();
        var keyByNormalizedTitle = new Dictionary<string, string>();
        for (var i = 0; i < inScope.Count; i++)
        {
            var a = inScope[i];
            var pair = (a.Category, a.Title);
            var n = keyCounts.TryGetValue(pair, out var count) ? count + 1 : 1;
            keyCounts[pair] = n;
            var key = n == 1 ? $"{a.Category}/{a.Title}" : $"{a.Category}/{a.Title}#{n}";
            keys[i] = key;

            var normalized = NormalizeTitle(a.Title);
            keyByNormalizedTitle.TryAdd(normalized, key); // first occurrence wins — file order
        }

        // Pass 2: build the two projections. Classification per achievement does not depend on any
        // other achievement, so this can run in one loop keyed off the pass-1 arrays.
        var meta = new List<SlayerMetaAchievement>();
        var kills = new List<SlayerKillAchievement>();
        for (var i = 0; i < inScope.Count; i++)
        {
            var a = inScope[i];
            var key = keys[i];

            if (IsMeta(a))
            {
                var components = a.Components
                    .Select(c =>
                    {
                        var title = ExtractTitle(c.Text);
                        var target = keyByNormalizedTitle.GetValueOrDefault(NormalizeTitle(title));
                        return new SlayerMetaComponent(title, c.Complete, c.Optional, target);
                    })
                    .ToList();
                var requiredTotal = a.Components.Count(c => !c.Optional);
                var requiredDone = a.Components.Count(c => !c.Optional && c.Complete);
                meta.Add(new SlayerMetaAchievement(key, a.Title, a.Complete, components, requiredDone, requiredTotal));
            }
            else
            {
                var components = a.Components
                    .Select(c => new SlayerKillComponent(c.Text, c.Complete, c.Optional, c.Have, c.Need))
                    .ToList();
                var tier = a.Category[CategoryPrefix.Length..].Trim();
                kills.Add(new SlayerKillAchievement(
                    key, tier, a.Title, a.Complete, components, Fraction(a), Remaining(a)));
            }
        }

        return new SlayerProgress(meta, kills);
    }

    // A meta-achievement is one whose components are *only* references to other achievements — an
    // achievement with no components at all counts as a kill achievement, not vacuously a meta one
    // (ADR-023 Decision 2 calls this out explicitly: "every" over an empty set is the wrong answer
    // here).
    private static bool IsMeta(Achievement a) =>
        a.Components.Count > 0 && a.Components.All(c => MetaReference.IsMatch(c.Text));

    private static string ExtractTitle(string componentText)
    {
        var match = MetaReference.Match(componentText);
        // IsMeta already required every component of this achievement to match, so this always
        // succeeds when called from the meta branch above.
        return match.Groups["title"].Value;
    }

    private static string NormalizeTitle(string title) => title.Trim().TrimEnd(TitleTrim).ToLowerInvariant();

    private static double Fraction(Achievement a)
    {
        // The export's own verdict outranks the components: a finished achievement can outlive a
        // component text change without this ever reading as anything but done.
        if (a.Complete)
        {
            return 1.0;
        }

        var required = a.Components.Where(c => !c.Optional).ToList();
        if (required.Count == 0)
        {
            return 0.0;
        }

        return required.Average(ComponentFraction);
    }

    private static double ComponentFraction(AchievementComponent c)
    {
        if (c.Complete)
        {
            return 1.0;
        }

        if (c.Have is { } have && c.Need is { } need && need > 0)
        {
            return Math.Clamp((double)have / need, 0.0, 1.0);
        }

        return 0.0;
    }

    private static int? Remaining(Achievement a)
    {
        var open = a.Components
            .Where(c => !c.Complete && !c.Optional && c.Have is not null && c.Need is not null)
            .ToList();
        return open.Count == 0 ? null : open.Sum(c => Math.Max(0, c.Need!.Value - c.Have!.Value));
    }
}
