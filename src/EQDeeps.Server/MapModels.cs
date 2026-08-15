using EQDeeps.Core.Maps;

namespace EQDeeps.Server;

/// <summary>
/// Every segment of one colour, packed as
/// <c>[x1,y1,z1, x2,y2,z2, …]</c>.
///
/// <para>Grouping by colour is not a compression trick, or not only one. A
/// canvas draws 26,000 segments quickly if it can stroke them as a handful of
/// paths and slowly if it has to change its stroke style between each; the
/// grouping the renderer wants is therefore the grouping the wire uses. It
/// happens to also cut the payload, since a map uses a few dozen colours across
/// tens of thousands of segments.</para>
///
/// <para>Z is carried per point rather than per group because it is what floor
/// filtering acts on — the same reason the layers are kept apart.</para>
/// </summary>
public sealed record MapStrokes(byte R, byte G, byte B, float[] Segments);

public sealed record MapLabelDto(float X, float Y, float Z, byte R, byte G, byte B, int Size, string Text);

public sealed record MapBoundsDto(
    float MinX, float MinY, float MinZ,
    float MaxX, float MaxY, float MaxZ);

public sealed record MapLayerDto(int Index, MapStrokes[] Strokes, MapLabelDto[] Labels, int Segments);

/// <param name="NameSource">
/// How the zone's display name was arrived at — <c>name</c>, <c>graph</c> or
/// <c>curated</c>. Surfaced rather than hidden because only the first two are
/// verifiable; see ADR-016.
/// </param>
/// <param name="Era">
/// The earliest expansion the place exists in, and <paramref name="EraSource"/>
/// how that was decided — the same pair the catalogue carries.
/// </param>
public sealed record ZoneMapDto(
    string ShortName,
    string? DisplayName,
    string? NameSource,
    string? Era,
    string? EraSource,
    string Set,
    IReadOnlyList<string> Sets,
    MapBoundsDto Bounds,
    MapLayerDto[] Layers)
{
    public static ZoneMapDto From(ZoneMap map, MapCatalogEntry entry, string set) => new(
        map.ShortName,
        entry.DisplayName,
        entry.NameSource,
        entry.Era,
        entry.EraSource,
        set,
        entry.Sets,
        new MapBoundsDto(
            map.Bounds.MinX, map.Bounds.MinY, map.Bounds.MinZ,
            map.Bounds.MaxX, map.Bounds.MaxY, map.Bounds.MaxZ),
        map.Layers.Select(Layer).ToArray());

    private static MapLayerDto Layer(MapLayer layer)
    {
        var strokes = layer.Lines
            .GroupBy(l => l.Color)
            .Select(g =>
            {
                var packed = new float[g.Count() * 6];
                var at = 0;

                foreach (var line in g)
                {
                    packed[at++] = line.From.X;
                    packed[at++] = line.From.Y;
                    packed[at++] = line.From.Z;
                    packed[at++] = line.To.X;
                    packed[at++] = line.To.Y;
                    packed[at++] = line.To.Z;
                }

                return new MapStrokes(g.Key.R, g.Key.G, g.Key.B, packed);
            })
            .ToArray();

        var labels = layer.Labels
            .Select(l => new MapLabelDto(
                l.At.X, l.At.Y, l.At.Z, l.Color.R, l.Color.G, l.Color.B, l.Size, l.Text))
            .ToArray();

        return new MapLayerDto(layer.Index, strokes, labels, layer.Lines.Count);
    }
}

/// <param name="Path">
/// A maps folder, or null/empty to clear the setting and return to discovery.
/// </param>
public sealed record SetMapRootRequest(string? Path);

/// <summary>A zone in the world graph, with the exits that were resolvable.</summary>
/// <param name="Era">
/// The earliest expansion the zone exists in, or absent when unknown. The
/// client does the hiding, because whether to hide or dim is a drawing
/// decision and toggling it should not cost a round trip.
/// </param>
public sealed record ZoneGraphNode(
    string ShortName,
    string? DisplayName,
    int Degree,
    string? Era,
    string? EraSource);

/// <summary>
/// An undirected connection, written once with the ends in a stable order so
/// the client never has to dedupe A→B against B→A.
/// </summary>
public sealed record ZoneGraphEdge(string From, string To);

/// <param name="Eras">
/// Every expansion in release order, so the client can order the nodes' era
/// codes without carrying its own copy of the list. Sent with the graph
/// because the graph is what it is for.
/// </param>
public sealed record ZoneGraphDto(ZoneGraphNode[] Zones, ZoneGraphEdge[] Edges, IReadOnlyList<ZoneEra> Eras);

/// <param name="Found">
/// Whether the labels join the two zones at all.
///
/// <para>This is a flag rather than a null <paramref name="Route"/> because the
/// app-wide serializer drops nulls (<c>ConfigureJson</c>), so "no route known"
/// would reach the client as a missing property — indistinguishable from a
/// response that failed to build. An empty route cannot carry the meaning
/// either: it reads as "you are already there".</para>
/// </param>
public sealed record ZoneRouteDto(bool Found, IReadOnlyList<ZoneRouteStep> Route)
{
    public static ZoneRouteDto NoRoute { get; } = new(false, Array.Empty<ZoneRouteStep>());
}

public sealed record ZoneRouteStep(string ShortName, string? DisplayName, string? Via);
