namespace EQDeeps.Core.Maps;

/// <summary>
/// One way out of a zone, as a mapmaker wrote it down.
/// </summary>
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
/// The world as a graph: zones for nodes, the maps' own <c>to_&lt;Zone&gt;</c>
/// labels for edges.
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

    private ZoneGraph(
        Dictionary<string, List<ZoneConnection>> outgoing,
        Dictionary<string, HashSet<string>> adjacency)
    {
        _out = outgoing;
        _adjacency = adjacency;
    }

    public IReadOnlyCollection<string> Zones => _adjacency.Keys;

    public int ConnectionCount => _out.Values.Sum(v => v.Count);

    /// <summary>Every labelled exit out of a zone. Empty for an unmapped or unlabelled zone.</summary>
    public IReadOnlyList<ZoneConnection> From(string shortName) =>
        _out.TryGetValue(shortName, out var list) ? list : Array.Empty<ZoneConnection>();

    /// <summary>Zones reachable in one step, in either written direction.</summary>
    public IReadOnlyCollection<string> Neighbours(string shortName) =>
        _adjacency.TryGetValue(shortName, out var set) ? set : Array.Empty<string>();

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

        var routable = all
            .Select(m => m.ShortName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> Adjacent(string zone)
        {
            if (!adjacency.TryGetValue(zone, out var set))
            {
                adjacency[zone] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return set;
        }

        foreach (var map in all)
        {
            Adjacent(map.ShortName);

            foreach (var label in map.Layers.SelectMany(l => l.Labels))
            {
                foreach (var destination in Destinations(label.Text))
                {
                    foreach (var target in table.MapsFor(destination))
                    {
                        if (string.Equals(target, map.ShortName, StringComparison.OrdinalIgnoreCase) ||
                            !routable.Contains(target))
                        {
                            continue;
                        }

                        if (!outgoing.TryGetValue(map.ShortName, out var list))
                        {
                            outgoing[map.ShortName] = list = new List<ZoneConnection>();
                        }

                        // One label can name several destinations; each becomes
                        // its own edge but they all keep the original text.
                        list.Add(new ZoneConnection(
                            map.ShortName,
                            target,
                            table.DisplayFor(target) ?? destination,
                            label.Text,
                            label.At));

                        Adjacent(map.ShortName).Add(target);
                        Adjacent(target).Add(map.ShortName);
                    }
                }
            }
        }

        return new ZoneGraph(outgoing, adjacency);
    }

    /// <summary>
    /// Pulls the destination zone names out of a label, or nothing if it is not
    /// a connection at all.
    ///
    /// <para>Both "to X" and "from X" count. A mapmaker standing at an arrival
    /// point writes "from", and the connection is the same either way — the
    /// graph is undirected regardless.</para>
    /// </summary>
    internal static IEnumerable<string> Destinations(string label)
    {
        var text = label.AsSpan().Trim();

        var body = text.StartsWith("to ", StringComparison.OrdinalIgnoreCase) ? text[3..]
            : text.StartsWith("from ", StringComparison.OrdinalIgnoreCase) ? text[5..]
            : default;

        if (body.IsEmpty)
        {
            yield break;
        }

        // "(Boat)", "(click the stone block)" — how to use the exit, not part
        // of the name. Everything from the first bracket is dropped.
        var bracket = body.IndexOf('(');
        if (bracket >= 0)
        {
            body = body[..bracket];
        }

        foreach (var part in body.ToString().Split(
            new[] { "/", " or ", " & ", " and ", "," },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = part.Trim().Trim('.', '`', '\'', '-', ' ');

            // Two characters cannot name a zone, and short fragments are where
            // truncated labels like "to Ak" land.
            if (name.Length > 2)
            {
                yield return name;
            }
        }
    }
}
