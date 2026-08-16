namespace EQDeeps.Core.Reference;

/// <summary>A name the index knows, and the variants listed under it.</summary>
public sealed record NpcNameMatch(string Name, IReadOnlyList<NpcIndexEntry> Variants);

/// <summary>
/// One row of a browse: a name, the span of levels it is listed at, and one
/// representative listing per level.
/// </summary>
public sealed record NpcNameRow(
    string Name,
    int? MinLevel,
    int? MaxLevel,
    /// <summary>How many listings the site carries under this name, before collapsing.</summary>
    int Listings,
    IReadOnlyList<NpcIndexEntry> PerLevel);

/// <summary>
/// The reference index in memory: every NPC name a site lists, searchable,
/// and resolvable to the one listing that matches a mob the log actually met.
///
/// <para><b>Why variants matter.</b> The same name is listed several times at
/// different levels — "a rabid kobold (6)" and "a rabid kobold (9)" are two
/// rows — because that is how the world is. The log gives a name and, when
/// the player consed it, a level. Checked against 60 of the owner's
/// most-killed mobs, picking the first listing by name alone put the listed
/// health within a sane distance of the measured damage-to-kill 55% of the
/// time; picking the variant whose level matches a /consider took that to
/// 60%, and moved the median ratio from 1.12 to 1.08 — which is about what
/// overkill alone should cost. So the level is the join key when there is
/// one, and the app says which listing it picked rather than pretending
/// there was only ever one.</para>
/// </summary>
public sealed class NpcIndex
{
    private readonly Dictionary<string, List<NpcIndexEntry>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _names = [];

    public NpcIndex(IEnumerable<NpcIndexEntry> entries)
    {
        foreach (var entry in entries)
        {
            var key = Normalize(entry.Name);
            if (key.Length == 0)
            {
                continue;
            }

            if (!_byName.TryGetValue(key, out var list))
            {
                list = [];
                _byName[key] = list;
                _names.Add(entry.Name);
            }

            list.Add(entry);
        }

        foreach (var list in _byName.Values)
        {
            list.Sort((a, b) => (a.Level ?? int.MaxValue).CompareTo(b.Level ?? int.MaxValue));
        }
    }

    public int NameCount => _byName.Count;

    public int EntryCount
    {
        get
        {
            var n = 0;
            foreach (var list in _byName.Values)
            {
                n += list.Count;
            }

            return n;
        }
    }

    /// <summary>
    /// The key a name is looked up under. The log writes a corpse's name
    /// verbatim ("a bandit") and a death normalized ("A bandit"), and a site
    /// lists whichever it likes, so case never decides; a trailing "'s
    /// corpse" is already stripped by the parser and is not handled here.
    /// </summary>
    public static string Normalize(string name) => name.Trim();

    /// <summary>Every listing under a name, cheapest question there is.</summary>
    public IReadOnlyList<NpcIndexEntry> Variants(string name) =>
        _byName.TryGetValue(Normalize(name), out var list) ? list : [];

    /// <summary>
    /// The listing that best matches a mob the log met: the variant whose
    /// level is nearest one the player consed, or — with no level to go on —
    /// the first listed. Null when the name is not listed at all.
    /// </summary>
    /// <param name="observedLevels">Levels a /consider reported for this mob; may be empty.</param>
    /// <param name="exact">
    /// True when a /consider corroborates the listing that was picked — its
    /// level is within a couple of one observed. False means nobody checked,
    /// or the nearest listing is far enough off to be a different mob wearing
    /// the same name. This is about corroboration, not about how the pick was
    /// made: a name with a single listing can still be corroborated, and
    /// usually is.
    /// </param>
    public NpcIndexEntry? Resolve(string name, IReadOnlyCollection<int> observedLevels, out bool exact)
    {
        exact = false;
        var variants = Variants(name);
        if (variants.Count == 0)
        {
            return null;
        }

        if (variants.Count == 1 || observedLevels.Count == 0)
        {
            exact = Corroborated(variants[0], observedLevels);
            return variants[0];
        }

        NpcIndexEntry? best = null;
        var bestDistance = int.MaxValue;
        foreach (var variant in variants)
        {
            if (variant.Level is not { } level)
            {
                continue;
            }

            foreach (var observed in observedLevels)
            {
                var distance = Math.Abs(level - observed);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = variant;
                }
            }
        }

        if (best is null)
        {
            exact = Corroborated(variants[0], observedLevels);
            return variants[0];
        }

        // A listing two levels off is still the right mob; ten levels off is a
        // different one wearing the same name, and saying so is more useful
        // than quietly showing its numbers.
        exact = bestDistance <= Tolerance;
        return best;
    }

    /// <summary>How far a listed level may sit from a considered one and still be the same mob.</summary>
    private const int Tolerance = 2;

    private static bool Corroborated(NpcIndexEntry entry, IReadOnlyCollection<int> observedLevels)
    {
        if (entry.Level is not { } level)
        {
            return false;
        }

        foreach (var observed in observedLevels)
        {
            if (Math.Abs(level - observed) <= Tolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Collapses a name's listings to one per level.
    ///
    /// <para>A site lists the same mob once per <i>zone</i> it stands in:
    /// "a ghoul" is 33 listings, 7 of them level 13, identical but for which
    /// zone they are placed in and a loot line or two. Browsing wants the mob,
    /// not its addresses — so a row is a name, its level span is stated, and
    /// the levels underneath are reachable one click in. Which zone a
    /// particular corpse came from is a question the log answers better than
    /// the index does.</para>
    /// </summary>
    public static IReadOnlyList<NpcIndexEntry> PerLevel(IReadOnlyList<NpcIndexEntry> variants)
    {
        var seen = new HashSet<int>();
        var kept = new List<NpcIndexEntry>();
        foreach (var variant in variants)
        {
            // Level-less listings are rare and never duplicated in practice;
            // they are kept as they come rather than folded into one another.
            if (variant.Level is not { } level)
            {
                kept.Add(variant);
                continue;
            }

            if (seen.Add(level))
            {
                kept.Add(variant);
            }
        }

        return kept;
    }

    /// <summary>Names matching a query, one row each, collapsed for browsing.</summary>
    public IReadOnlyList<NpcNameRow> Browse(string query, int limit = 100)
    {
        var rows = new List<NpcNameRow>();
        foreach (var match in Search(query, limit))
        {
            var levels = match.Variants.Select(v => v.Level).Where(l => l is not null).Select(l => l!.Value).ToArray();
            rows.Add(new NpcNameRow(
                match.Name,
                levels.Length > 0 ? levels.Min() : null,
                levels.Length > 0 ? levels.Max() : null,
                match.Variants.Count,
                PerLevel(match.Variants)));
        }

        return rows;
    }

    /// <summary>
    /// Names matching a query, best first: an exact hit, then names that start
    /// with it, then names that merely contain it, alphabetical within each
    /// band. Substring rather than the fuzzy subsequence the tables use —
    /// nine thousand names make a subsequence match far too generous ("abc"
    /// would find half the list).
    /// </summary>
    public IReadOnlyList<NpcNameMatch> Search(string query, int limit = 100)
    {
        var q = query.Trim();
        if (q.Length == 0)
        {
            return [];
        }

        var exact = new List<string>();
        var prefix = new List<string>();
        var contains = new List<string>();
        foreach (var name in _names)
        {
            if (name.Equals(q, StringComparison.OrdinalIgnoreCase))
            {
                exact.Add(name);
            }
            else if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            {
                prefix.Add(name);
            }
            else if (name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                contains.Add(name);
            }
        }

        prefix.Sort(StringComparer.OrdinalIgnoreCase);
        contains.Sort(StringComparer.OrdinalIgnoreCase);

        var results = new List<NpcNameMatch>(Math.Min(limit, exact.Count + prefix.Count + contains.Count));
        foreach (var name in exact.Concat(prefix).Concat(contains))
        {
            if (results.Count >= limit)
            {
                break;
            }

            results.Add(new NpcNameMatch(name, Variants(name)));
        }

        return results;
    }
}
