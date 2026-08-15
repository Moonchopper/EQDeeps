namespace EQDeeps.Core.Maps;

/// <summary>
/// A point in EverQuest world space, exactly as the map file writes it — no
/// reorientation, no scaling.
///
/// <para>Note that this is <em>not</em> the order the game says coordinates in.
/// <c>/loc</c> prints Y, X, Z; the map files write X, Y, Z. Converting between
/// the two is a display concern and is deliberately not done here, so that what
/// is parsed can be compared against the file it came from.</para>
/// </summary>
public readonly record struct MapPoint(float X, float Y, float Z);

/// <summary>An RGB triple straight out of the map file, 0–255 per channel.</summary>
public readonly record struct MapColor(byte R, byte G, byte B);

/// <summary>One drawn segment. The overwhelming majority of a map file is these.</summary>
public sealed record MapLine(MapPoint From, MapPoint To, MapColor Color);

/// <summary>
/// A labelled point of interest — an NPC, a note, or a zone connection.
/// </summary>
/// <param name="Size">
/// The file's own size field. Its meaning is unspecified and the corpus uses it
/// inconsistently (0 dominates, then 200 and 240), so it is carried through
/// rather than interpreted as a font size.
/// </param>
/// <param name="Text">
/// The label with underscores restored to spaces, which is the format's
/// convention for encoding a space.
/// </param>
public sealed record MapLabel(MapPoint At, MapColor Color, int Size, string Text);

/// <summary>
/// The extent of a map, used to frame it on screen. Degenerate when the layer
/// held nothing drawable, which is why <see cref="IsEmpty"/> exists rather than
/// callers testing the numbers themselves.
/// </summary>
public readonly record struct MapBounds(
    float MinX,
    float MinY,
    float MinZ,
    float MaxX,
    float MaxY,
    float MaxZ)
{
    public bool IsEmpty => MinX > MaxX;

    /// <summary>The bounds that contain nothing, and absorb whatever is added to them.</summary>
    public static MapBounds Empty => new(
        float.MaxValue, float.MaxValue, float.MaxValue,
        float.MinValue, float.MinValue, float.MinValue);

    public MapBounds Add(MapPoint p) => new(
        Math.Min(MinX, p.X), Math.Min(MinY, p.Y), Math.Min(MinZ, p.Z),
        Math.Max(MaxX, p.X), Math.Max(MaxY, p.Y), Math.Max(MaxZ, p.Z));

    public MapBounds Union(MapBounds other) => other.IsEmpty ? this : IsEmpty ? other : new(
        Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY), Math.Min(MinZ, other.MinZ),
        Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY), Math.Max(MaxZ, other.MaxZ));
}

/// <summary>
/// The contents of one map file.
///
/// <para>EverQuest splits a zone across up to four files — <c>gukbottom.txt</c>
/// plus <c>_1</c>, <c>_2</c>, <c>_3</c> — and the client draws them as
/// independently toggleable layers. They are kept apart here for the same
/// reason: the layer split is usually floors or annotation density, and merging
/// them would throw away the only handle the user has on a cluttered map.</para>
/// </summary>
/// <param name="Malformed">
/// Records that did not parse. Counted rather than thrown, matching the rule
/// the log parser follows: these are user-editable community files and a single
/// bad line must not cost the whole zone.
/// </param>
public sealed record MapLayer(
    int Index,
    IReadOnlyList<MapLine> Lines,
    IReadOnlyList<MapLabel> Labels,
    MapBounds Bounds,
    int Malformed)
{
    public static MapLayer Empty(int index) => new(
        index, Array.Empty<MapLine>(), Array.Empty<MapLabel>(), MapBounds.Empty, 0);
}

/// <summary>
/// Every layer found for one zone, keyed by the map file's short name
/// (<c>gukbottom</c>) — which is the only name the files themselves carry. The
/// display name the log speaks in is resolved separately; see
/// <see cref="ZoneTable"/> for why that join needs its own table.
/// </summary>
public sealed record ZoneMap(
    string ShortName,
    IReadOnlyList<MapLayer> Layers,
    MapBounds Bounds)
{
    public int LineCount => Layers.Sum(l => l.Lines.Count);

    public int LabelCount => Layers.Sum(l => l.Labels.Count);

    public int Malformed => Layers.Sum(l => l.Malformed);

    public static ZoneMap FromLayers(string shortName, IReadOnlyList<MapLayer> layers)
    {
        var bounds = layers.Aggregate(MapBounds.Empty, (acc, l) => acc.Union(l.Bounds));
        return new ZoneMap(shortName, layers, bounds);
    }
}
