using EQDeeps.Core.Maps;
using Xunit;

namespace EQDeeps.Core.Tests;

public class MapFileParserTests
{
    [Fact]
    public void ParsesLineSegments()
    {
        var layer = MapFileParser.Parse(
            "L 503.4496, 155.7608, 1.0010,  505.4496, 155.7737, 2.0010,  64, 64, 64");

        var line = Assert.Single(layer.Lines);
        Assert.Equal(503.4496f, line.From.X, 3);
        Assert.Equal(155.7608f, line.From.Y, 3);
        Assert.Equal(1.0010f, line.From.Z, 3);
        Assert.Equal(505.4496f, line.To.X, 3);
        Assert.Equal(new MapColor(64, 64, 64), line.Color);
        Assert.Equal(0, layer.Malformed);
    }

    [Fact]
    public void ParsesLabelsAndRestoresSpaces()
    {
        var layer = MapFileParser.Parse(
            "P 32.9258, -1510.4930, -99.7718,  150, 0, 200,  3,  to_The_City_of_Guk");

        var label = Assert.Single(layer.Labels);
        Assert.Equal("to The City of Guk", label.Text);
        Assert.Equal(3, label.Size);
        Assert.Equal(new MapColor(150, 0, 200), label.Color);
        Assert.Equal(-1510.4930f, label.At.Y, 3);
    }

    /// <summary>
    /// 1660 labels in a stock install carry a comma. Taking the eighth
    /// comma-separated field instead of "the rest of the record" truncates
    /// every one of them to the part before the comma.
    /// </summary>
    [Fact]
    public void KeepsCommasInsideLabels()
    {
        var layer = MapFileParser.Parse(
            "P 770.1974, -12.3611, 68.7689,  0, 0, 240,  2,  Draton`ra,_Master_of_the_Void");

        Assert.Equal("Draton`ra, Master of the Void", Assert.Single(layer.Labels).Text);
    }

    /// <summary>
    /// Some files drop the newline between two records. The client draws both,
    /// so losing them here would quietly delete map detail the player can see
    /// in game.
    /// </summary>
    [Fact]
    public void SplitsRecordsThatRunTogether()
    {
        var layer = MapFileParser.Parse(
            "L 368.8268, 2320.9848, 1951.6470, 368.8268, 2320.9848, 1951.6470, 0, 0, 0"
            + "P -178.0000, -207.0000, -1624.3743, 255, 0, 0, 3, from_The_Plane_of_Tranquility");

        Assert.Single(layer.Lines);
        Assert.Equal("from The Plane of Tranquility", Assert.Single(layer.Labels).Text);
        Assert.Equal(0, layer.Malformed);
    }

    [Fact]
    public void CountsMalformedRecordsRatherThanThrowing()
    {
        var layer = MapFileParser.Parse(
            """
            L 1, 2, 3, 4, 5, 6, 7, 8, 9
            L not, a, number
            X 1, 2, 3
            P 1, 2, 3, 4, 5, 6, 7, fine
            """);

        Assert.Single(layer.Lines);
        Assert.Single(layer.Labels);
        Assert.Equal(2, layer.Malformed);
    }

    [Fact]
    public void IgnoresBlankLinesAndCarriageReturns()
    {
        var layer = MapFileParser.Parse("L 1, 2, 3, 4, 5, 6, 7, 8, 9\r\n\r\n");

        Assert.Single(layer.Lines);
        Assert.Equal(0, layer.Malformed);
    }

    [Fact]
    public void BoundsCoverEveryDrawnPoint()
    {
        var layer = MapFileParser.Parse(
            """
            L -10, 5, 0, 20, -30, 4, 0, 0, 0
            P 100, 200, -50, 0, 0, 0, 3, Somewhere
            """);

        Assert.Equal(-10, layer.Bounds.MinX);
        Assert.Equal(100, layer.Bounds.MaxX);
        Assert.Equal(-30, layer.Bounds.MinY);
        Assert.Equal(200, layer.Bounds.MaxY);
        Assert.Equal(-50, layer.Bounds.MinZ);
        Assert.Equal(4, layer.Bounds.MaxZ);
    }

    [Fact]
    public void EmptyTextIsAnEmptyLayer()
    {
        var layer = MapFileParser.Parse("");

        Assert.Empty(layer.Lines);
        Assert.Empty(layer.Labels);
        Assert.True(layer.Bounds.IsEmpty);
        Assert.Equal(0, layer.Malformed);
    }

    /// <summary>
    /// Colour channels are clamped rather than rejected: the corpus is
    /// hand-edited and an out-of-range channel is a cosmetic defect, not a
    /// reason to drop a zone's geometry.
    /// </summary>
    [Fact]
    public void ClampsOutOfRangeColourChannels()
    {
        var layer = MapFileParser.Parse("L 0, 0, 0, 1, 1, 1, -20, 300, 128.9");

        Assert.Equal(new MapColor(0, 255, 128), Assert.Single(layer.Lines).Color);
    }
}
