namespace EQDeeps.Core.Reference;

/// <summary>A name the index knows, and the variants listed under it.</summary>
public sealed record NpcNameMatch(string Name, IReadOnlyList<NpcIndexEntry> Variants);

/// <summary>
/// One row of a browse: a name, the span of levels it is listed at, one
/// representative listing per level — and every listing, for anyone who
/// needs to know where they all are.
/// </summary>
public sealed record NpcNameRow(
    string Name,
    int? MinLevel,
    int? MaxLevel,
    /// <summary>How many listings the site carries under this name, before collapsing.</summary>
    int Listings,
    IReadOnlyList<NpcIndexEntry> PerLevel,
    IReadOnlyList<NpcIndexEntry> Variants);

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
    /// <summary>Every name, as the site prints it, with the key it is filed under beside it.</summary>
    private readonly List<(string Name, string Key)> _names = [];
    /// <summary>Where each key's name sits in <see cref="_names"/>, so a later listing can replace the printed form.</summary>
    private readonly Dictionary<string, int> _nameAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NpcZoneRow> _zones = new(StringComparer.OrdinalIgnoreCase);

    public NpcIndex(IEnumerable<NpcIndexEntry> entries, IEnumerable<NpcZoneRow>? zones = null)
    {
        foreach (var zone in zones ?? [])
        {
            _zones.TryAdd(zone.ShortName, zone);
        }

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
                _nameAt[key] = _names.Count;
                _names.Add((entry.Name.Trim(), key));
            }
            else if (HasArticle(entry.Name) && !HasArticle(_names[_nameAt[key]].Name))
            {
                // Listed both ways ("crypt mummy" and "a crypt mummy" are 26
                // such names on EQLBase): one row, shown under the form the
                // game itself uses, whichever the site happened to print first.
                _names[_nameAt[key]] = (entry.Name.Trim(), key);
            }

            list.Add(entry);
        }

        foreach (var list in _byName.Values)
        {
            list.Sort((a, b) => (a.Level ?? int.MaxValue).CompareTo(b.Level ?? int.MaxValue));
        }
    }

    public int NameCount => _byName.Count;

    /// <summary>The zones the site itself lists, by its short name for each. Empty for an index without zone rows.</summary>
    public IReadOnlyCollection<NpcZoneRow> Zones => _zones.Values;

    /// <summary>The site's own row for a zone short name, or null when it lists none.</summary>
    public NpcZoneRow? Zone(string shortName) =>
        _zones.TryGetValue(shortName, out var zone) ? zone : null;

    /// <summary>Every listing, in no particular order — for the derivations that walk the whole index (<see cref="ZoneLevels"/>).</summary>
    public IEnumerable<NpcIndexEntry> Entries => _byName.Values.SelectMany(list => list);

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
    ///
    /// <para>Nor does the article. The game names its generic mobs with one
    /// — "an imp protector", "a centaur archer" — and the log repeats it,
    /// but a site drops it when it feels like it: of the ~3,400 lower-case
    /// names EQLBase lists, 354 have no article (every aqua goblin, aviak,
    /// centaur, cinder goblin and clockwork among them) and 26 more are
    /// listed both ways. Keyed verbatim, a tenth of the mobs a log can name
    /// resolved to nothing and a click on one did nothing. So a leading
    /// "a", "an" or "the" is not part of a name's identity here, on either
    /// side; two site rows that differ only by it are one name.</para>
    /// </summary>
    public static string Normalize(string name) => StripArticle(name.Trim());

    /// <summary>
    /// The name without a leading article, or as given when it has none (or
    /// is nothing but one — "a" is a query, not an article).
    /// </summary>
    internal static string StripArticle(string name)
    {
        foreach (var article in Articles)
        {
            if (name.Length > article.Length &&
                name.StartsWith(article, StringComparison.OrdinalIgnoreCase) &&
                char.IsWhiteSpace(name[article.Length]))
            {
                var rest = name.AsSpan(article.Length).TrimStart();
                return rest.Length > 0 ? rest.ToString() : name;
            }
        }

        return name;
    }

    private static readonly string[] Articles = ["an", "a", "the"];

    private static bool HasArticle(string name)
    {
        var trimmed = name.Trim();
        return !ReferenceEquals(StripArticle(trimmed), trimmed);
    }

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

    /// <summary>
    /// Names matching a query, one row each, collapsed for browsing —
    /// optionally only those with a listing inside a level band, which is
    /// also how the whole index is browsed with no query at all.
    /// </summary>
    /// <param name="minLevel">Lowest listed level to keep, inclusive; null for no floor.</param>
    /// <param name="maxLevel">Highest listed level to keep, inclusive; null for no ceiling.</param>
    public IReadOnlyList<NpcNameRow> Browse(string query, int limit = 100, int? minLevel = null, int? maxLevel = null)
    {
        var rows = new List<NpcNameRow>();
        foreach (var match in Search(query, limit, minLevel, maxLevel))
        {
            var levels = match.Variants.Select(v => v.Level).Where(l => l is not null).Select(l => l!.Value).ToArray();
            rows.Add(new NpcNameRow(
                match.Name,
                levels.Length > 0 ? levels.Min() : null,
                levels.Length > 0 ? levels.Max() : null,
                match.Variants.Count,
                PerLevel(match.Variants),
                match.Variants));
        }

        return rows;
    }

    /// <summary>
    /// Names matching a query, best first: an exact hit, then names that start
    /// with it, then names that merely contain it, alphabetical within each
    /// band. Substring rather than the fuzzy subsequence the tables use —
    /// nine thousand names make a subsequence match far too generous ("abc"
    /// would find half the list).
    ///
    /// <para>A level band narrows the same search to names with at least one
    /// listing inside it — the level filter is on the mob, not on the name, so
    /// "a ghoul" (13–24) is in the 20s band by its level-24 listing. With a
    /// band and no query, every name in the band comes back alphabetically:
    /// that is what "browse the level 20s" means. With neither, nothing —
    /// nine thousand names is not an answer to any question.</para>
    /// </summary>
    public IReadOnlyList<NpcNameMatch> Search(string query, int limit = 100, int? minLevel = null, int? maxLevel = null)
    {
        var q = query.Trim();
        var banded = minLevel is not null || maxLevel is not null;
        if (q.Length == 0 && !banded)
        {
            return [];
        }

        // Exact and prefix are judged with the article off both sides, so
        // "An imp protector" is an exact hit on a site's "imp protector" and
        // "imp" is a prefix of "an imp protector"; contains is on the printed
        // name, where the article is just more letters to match.
        var sq = StripArticle(q);
        var exact = new List<(string Name, string Key)>();
        var prefix = new List<(string Name, string Key)>();
        var contains = new List<(string Name, string Key)>();
        foreach (var entry in _names)
        {
            var (name, key) = entry;
            if (banded && !InBand(name, minLevel, maxLevel))
            {
                continue;
            }

            if (q.Length == 0 || key.Equals(sq, StringComparison.OrdinalIgnoreCase))
            {
                exact.Add(entry);
            }
            else if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
                     key.StartsWith(sq, StringComparison.OrdinalIgnoreCase))
            {
                prefix.Add(entry);
            }
            else if (name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                contains.Add(entry);
            }
        }

        // With no query the "exact" band is the whole answer, so it is the one
        // that needs ordering; with one there is at most one exact hit. Ordered
        // by the key, not the printed name, so a browse of the twenties files
        // "an aqua goblin" under A-Q-U-A beside "aqua goblin bosun" rather
        // than under A-N with every other "an".
        static int ByKey((string Name, string Key) a, (string Name, string Key) b) =>
            StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key);
        exact.Sort(ByKey);
        prefix.Sort(ByKey);
        contains.Sort(ByKey);

        var results = new List<NpcNameMatch>(Math.Min(limit, exact.Count + prefix.Count + contains.Count));
        foreach (var (name, _) in exact.Concat(prefix).Concat(contains))
        {
            if (results.Count >= limit)
            {
                break;
            }

            results.Add(new NpcNameMatch(name, Variants(name)));
        }

        return results;
    }

    /// <summary>How many names have a listing inside a band — the count a browse says it is showing part of.</summary>
    public int CountInBand(int? minLevel, int? maxLevel) =>
        _names.Count(n => InBand(n.Name, minLevel, maxLevel));

    private bool InBand(string name, int? minLevel, int? maxLevel)
    {
        foreach (var variant in Variants(name))
        {
            if (variant.Level is not { } level)
            {
                continue;
            }

            if ((minLevel is null || level >= minLevel) && (maxLevel is null || level <= maxLevel))
            {
                return true;
            }
        }

        return false;
    }
}
