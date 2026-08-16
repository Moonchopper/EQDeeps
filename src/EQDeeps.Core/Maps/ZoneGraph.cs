namespace EQDeeps.Core.Maps;

/// <summary>
/// One way out of a zone, as a mapmaker wrote it down.
/// </summary>
/// <param name="FromShortName">
/// The map file that carried the label — the actual drawing, not the place it
/// belongs to, because that is where the point <paramref name="At"/> lives.
/// </param>
/// <param name="ToShortName">
/// The place it leads to, as that place's representative short name (see
/// <see cref="ZoneGraph"/>): a label to "West Freeport" lands on the one West
/// Freeport node, whichever of its two drawings the player has open.
/// </param>
/// <param name="Label">
/// The raw label, kept because it carries the part the graph throws away — a
/// parenthetical like "(Boat)" or "(click stone block)" is often the only
/// statement of *how* the connection is used.
/// </param>
/// <param name="At">Where the exit is, so the map can point at it.</param>
public sealed record ZoneConnection(
    string FromShortName,
    string ToShortName,
    string ToDisplayName,
    string Label,
    MapPoint At);

/// <summary>
/// The world as a graph: places for nodes, the maps' own <c>to_&lt;Zone&gt;</c>
/// labels for edges.
///
/// <para><b>A node is a place, not a file.</b> Several maps can carry one
/// display name — <c>freportw</c> and <c>freeportwest</c> are both "West
/// Freeport", <c>hateplane</c> and <c>hateplaneb</c> both "The Plane of Hate"
/// — and a label naming that place resolves to every map that claims it. Drawn
/// one node per file, every such place appeared twice, each copy wired to the
/// same neighbours, and the Plane of Hate looked like two zones off the Oasis
/// of Marr. So maps sharing a display name are one node, identified by the
/// first of their short names in the order the maps were given (the catalogue's
/// order, so it agrees with what the zone list opens), and the node's exits are
/// the union of its drawings' labels — the same reason both map <em>sets</em>
/// are read: which drawing you look at is taste, which exits exist is not.
/// <see cref="MapsOf"/> gives the drawings back; <see cref="PlaceOf"/> takes
/// any of them to the node, so callers may still speak in file names.</para>
///
/// <para>The edges are community annotation, not game data, and they read like
/// it — inconsistent apostrophes, "(Boat)" suffixes, and points that name three
/// destinations at once ("to Butcherblock/Ocean of Tears/Qeynos"). Every label
/// is therefore resolved through <see cref="ZoneTable"/> and dropped if it does
/// not land on a zone the client actually has a name for. That is a deliberate
/// bias toward a smaller, truthful graph: a route the app cannot describe is
/// better than one it invents.</para>
///
/// <para>Edges are treated as undirected for routing. A mapmaker labels the
/// side of the connection they were standing on, so roughly half of every real
/// pair is written down only once; requiring both directions would fragment the
/// world into islands.</para>
/// </summary>
public sealed class ZoneGraph
{
    private readonly Dictionary<string, List<ZoneConnection>> _out;
    private readonly Dictionary<string, HashSet<string>> _adjacency;
    private readonly Dictionary<string, List<string>> _maps;
    private readonly Dictionary<string, string> _placeOf;

    private ZoneGraph(
        Dictionary<string, List<ZoneConnection>> outgoing,
        Dictionary<string, HashSet<string>> adjacency,
        Dictionary<string, List<string>> maps,
        Dictionary<string, string> placeOf)
    {
        _out = outgoing;
        _adjacency = adjacency;
        _maps = maps;
        _placeOf = placeOf;
    }

    /// <summary>Every place, by its representative short name.</summary>
    public IReadOnlyCollection<string> Zones => _adjacency.Keys;

    public int ConnectionCount => _out.Values.Sum(v => v.Count);

    /// <summary>
    /// The map short names that draw a place, representative first. A single
    /// entry for most places; two for a revamp kept beside its original.
    /// </summary>
    public IReadOnlyList<string> MapsOf(string place) =>
        _maps.TryGetValue(Canonical(place), out var list) ? list : Array.Empty<string>();

    /// <summary>
    /// The place a map belongs to, or null if the map is not in the graph.
    /// Every other lookup here accepts a map short name and resolves it this
    /// way, so nothing has to know which of two drawings is the representative.
    /// </summary>
    public string? PlaceOf(string shortName) =>
        _placeOf.TryGetValue(shortName, out var place) ? place : null;

    private string Canonical(string shortName) => PlaceOf(shortName) ?? shortName;

    /// <summary>
    /// Every labelled exit out of a place, across all of its drawings. Empty
    /// for an unmapped or unlabelled zone.
    /// </summary>
    public IReadOnlyList<ZoneConnection> From(string shortName) =>
        _out.TryGetValue(Canonical(shortName), out var list) ? list : Array.Empty<ZoneConnection>();

    /// <summary>Places reachable in one step, in either written direction.</summary>
    public IReadOnlyCollection<string> Neighbours(string shortName) =>
        _adjacency.TryGetValue(Canonical(shortName), out var set) ? set : Array.Empty<string>();

    /// <summary>
    /// The fewest-zones route from one short name to another, inclusive of
    /// both ends, or null when there is no path through the labels we have.
    ///
    /// <para>Breadth-first and unweighted: the graph has no travel times in it,
    /// so "fewest zones" is the only honest ordering. Neighbours are visited in
    /// name order to keep the answer stable between runs — an arbitrary but
    /// reproducible route beats one that changes each time the maps are
    /// re-read.</para>
    /// </summary>
    /// <param name="allowed">
    /// Which zones the route may use, ends included; null allows every zone.
    /// This is how the era filter reaches routing: a route through a zone the
    /// server has not unlocked is worse than "no route known", so such zones
    /// are simply not there to be walked. The predicate is asked once per zone,
    /// so it can be as slow as a table lookup without hurting the search.
    /// </param>
    public IReadOnlyList<string>? Route(string from, string to, Func<string, bool>? allowed = null)
    {
        allowed ??= static _ => true;

        // Either end may be named by any of its drawings; the route is in places.
        from = Canonical(from);
        to = Canonical(to);

        if (!_adjacency.ContainsKey(from) || !_adjacency.ContainsKey(to)
            || !allowed(from) || !allowed(to))
        {
            return null;
        }

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            return new[] { from };
        }

        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { from };
        var queue = new Queue<string>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var next in Neighbours(current).OrderBy(n => n, StringComparer.Ordinal))
            {
                // Marked seen before the era check so a disallowed zone is
                // asked about once, not once per neighbour that reaches it.
                if (!seen.Add(next) || !allowed(next))
                {
                    continue;
                }

                previous[next] = current;

                if (string.Equals(next, to, StringComparison.OrdinalIgnoreCase))
                {
                    return Rebuild(previous, from, to);
                }

                queue.Enqueue(next);
            }
        }

        return null;
    }

    private static List<string> Rebuild(Dictionary<string, string> previous, string from, string to)
    {
        var path = new List<string> { to };
        var at = to;

        while (!string.Equals(at, from, StringComparison.OrdinalIgnoreCase))
        {
            at = previous[at];
            path.Add(at);
        }

        path.Reverse();
        return path;
    }

    /// <summary>
    /// Builds the graph from already-parsed maps. Takes the maps rather than a
    /// folder so this stays testable without a game install, matching the rule
    /// the log grammars follow.
    /// </summary>
    public static ZoneGraph Build(IEnumerable<ZoneMap> maps, ZoneTable table)
    {
        var outgoing = new Dictionary<string, List<ZoneConnection>>(StringComparer.OrdinalIgnoreCase);
        var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var placeMaps = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var placeOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Only zones this machine can both draw and name take part.
        //
        // Drawable, because the table maps one display name onto every map that
        // claims it — "The Ocean of Tears" is both oot and oceanoftears — and an
        // edge to a zone with no map here is a route the player cannot be shown.
        //
        // Nameable, because a map set is full of alternates and archives:
        // nektulos_1_original, oldcommons, feerrott2. They are real files and
        // worth drawing, but a route that says "go through nektulos_1_original"
        // names a file rather than a place. Restricting to zones the table names
        // is what keeps a route something a player can follow.
        var all = (maps as IReadOnlyCollection<ZoneMap> ?? maps.ToList())
            .Where(m => table.DisplayFor(m.ShortName) is not null)
            .ToList();

        // Maps that share a display name are one place. The first drawing seen
        // stands for the place, so the order the maps come in — the catalogue's
        // — decides the name a route step carries.
        var byDisplay = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var map in all)
        {
            var display = table.DisplayFor(map.ShortName)!;
            if (!byDisplay.TryGetValue(display, out var place))
            {
                byDisplay[display] = place = map.ShortName;
                placeMaps[place] = new List<string>();
            }

            placeMaps[place].Add(map.ShortName);
            placeOf[map.ShortName] = place;
        }

        HashSet<string> Adjacent(string place)
        {
            if (!adjacency.TryGetValue(place, out var set))
            {
                adjacency[place] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return set;
        }

        foreach (var map in all)
        {
            var here = placeOf[map.ShortName];
            Adjacent(here);

            foreach (var label in map.Layers.SelectMany(l => l.Labels))
            {
                foreach (var destination in Destinations(label.Text))
                {
                    // Every drawing of the destination lands on the same place,
                    // so one label to "The Plane of Hate" is one edge, not one
                    // per file that claims the name.
                    var targets = table.MapsFor(destination)
                        .Where(placeOf.ContainsKey)
                        .Select(t => placeOf[t])
                        .Distinct(StringComparer.OrdinalIgnoreCase);

                    foreach (var target in targets)
                    {
                        if (string.Equals(target, here, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!outgoing.TryGetValue(here, out var list))
                        {
                            outgoing[here] = list = new List<ZoneConnection>();
                        }

                        // One label can name several destinations; each becomes
                        // its own edge but they all keep the original text.
                        list.Add(new ZoneConnection(
                            map.ShortName,
                            target,
                            table.DisplayFor(target) ?? destination,
                            label.Text,
                            label.At));

                        Adjacent(here).Add(target);
                        Adjacent(target).Add(here);
                    }
                }
            }
        }

        return new ZoneGraph(outgoing, adjacency, placeMaps, placeOf);
    }

    /// <summary>
    /// Pulls the destination zone names out of a label, or nothing if it is not
    /// a connection at all.
    ///
    /// <para>Both "to X" and "from X" count. A mapmaker standing at an arrival
    /// point writes "from", and the connection is the same either way — the
    /// graph is undirected regardless.</para>
    ///
    /// <para>The connection word need not come first. The client's own East
    /// Freeport map writes the way to the Plane of Sky as
    /// <c>portal to The Plane of Sky (click)</c>; the Fear portal is
    /// <c>portal to The Plane of Fear</c> from one side and <c>Zone In from
    /// Feerrott</c> from the other; West Freeport has <c>Teleport to Academy
    /// of Arcane Sciences</c>. Anchoring on a leading "to" missed every one
    /// of them — the exits that are an object rather than a zone line, which
    /// are exactly the ones the in-game atlas is also weakest on. So the word
    /// is looked for anywhere in the label, with two guards the survey of the
    /// corpus called for. The parenthetical is dropped <i>first</i>: a "to"
    /// inside it is about how the point is used ("Hunter, Paths To Arena" on
    /// a Riwwi mob is a mob's patrol, not a way to the Arena). And when the
    /// word is not first, what follows it must read as a proper name — a note
    /// like "complete the event to open the floor" or "back to entrance" is
    /// prose, and prose does not name zones in lower case. Of the 280 such
    /// labels across both map sets, 7 resolve to a zone the client names,
    /// and all 7 are real.</para>
    /// </summary>
    internal static IEnumerable<string> Destinations(string label)
    {
        var text = label.AsSpan().Trim();

        // "(Boat)", "(click the stone block)" — how to use the exit, not part
        // of the name, and never where the connection word is looked for.
        // Everything from the first bracket is dropped.
        var bracket = text.IndexOf('(');
        if (bracket >= 0)
        {
            text = text[..bracket];
        }

        ReadOnlySpan<char> body;
        var leading = true;
        if (text.StartsWith("to ", StringComparison.OrdinalIgnoreCase))
        {
            body = text[3..];
        }
        else if (text.StartsWith("from ", StringComparison.OrdinalIgnoreCase))
        {
            body = text[5..];
        }
        else
        {
            var to = text.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
            var from = text.IndexOf(" from ", StringComparison.OrdinalIgnoreCase);
            var at = to < 0 ? from : from < 0 ? to : Math.Min(to, from);
            if (at < 0)
            {
                yield break;
            }

            body = text[(at + (at == to ? 4 : 6))..];
            leading = false;
        }

        if (body.IsEmpty)
        {
            yield break;
        }

        foreach (var part in body.ToString().Split(
            new[] { "/", " or ", " & ", " and ", "," },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = part.Trim().Trim('.', '`', '\'', '-', ' ');

            // Two characters cannot name a zone, and short fragments are where
            // truncated labels like "to Ak" land.
            if (name.Length <= 2)
            {
                continue;
            }

            // A destination reached from inside a sentence has to look like a
            // name. The leading form keeps its old tolerance: "to innothule
            // swamp" is a connection however it is cased.
            if (!leading && !char.IsUpper(name[0]))
            {
                continue;
            }

            yield return name;
        }
    }
}
