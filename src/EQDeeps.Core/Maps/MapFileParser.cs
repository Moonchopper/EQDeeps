using System.Globalization;

namespace EQDeeps.Core.Maps;

/// <summary>
/// Reads EverQuest's map format: one record per line, either a line segment or
/// a labelled point.
///
/// <code>
/// L x1, y1, z1, x2, y2, z2, r, g, b
/// P x, y, z, r, g, b, size, Label_With_Underscores
/// </code>
///
/// <para>Pure function of the text, the same rule the log grammars follow — no
/// file access here, so the whole format is testable from string literals and
/// the fixture corpus can stand in for a game install.</para>
///
/// <para>Two properties of the real corpus drive the implementation, and both
/// were measured across the ~1900 files a stock install ships rather than
/// assumed:</para>
///
/// <list type="bullet">
/// <item><description><b>Labels contain commas.</b> 1660 of them do
/// (<c>Draton`ra,_Master_of_the_Void</c>). Splitting a P record on commas and
/// taking field 8 truncates every one, so the label is defined as everything
/// after the seventh comma instead.</description></item>
/// <item><description><b>Records run together.</b> A handful of files drop the
/// newline between two records, leaving <c>…, 0, 0, 0P -178.0000, …</c>. The
/// client tolerates it, so the corpus still contains it; a parser that splits
/// only on newlines silently loses both records.</description></item>
/// </list>
/// </summary>
public static class MapFileParser
{
    /// <summary>
    /// Guards against a file that is not a map at all being read as one —
    /// a stray binary would otherwise buy an allocation per "line".
    /// </summary>
    private const int MaxRecordLength = 512;

    /// <param name="labelsOnly">
    /// Skip the geometry and read only the labelled points.
    ///
    /// <para>For the world graph, which needs a zone's exits and nothing it
    /// draws. Segments are 99% of the corpus — 3,244,827 of them against 35,719
    /// labels — so parsing them to discard them dominated the graph build.
    /// Skipping them is not merely an optimisation of degree: it is the
    /// difference between reading a map and reading its index.</para>
    /// </param>
    public static MapLayer Parse(string text, int layerIndex = 0, bool labelsOnly = false)
    {
        var lines = new List<MapLine>();
        var labels = new List<MapLabel>();
        var bounds = MapBounds.Empty;
        var malformed = 0;

        foreach (var record in Records(text))
        {
            if (record.Length > MaxRecordLength)
            {
                malformed++;
                continue;
            }

            switch (record[0])
            {
                // Skipped by request is not the same as unparseable, so this
                // must not reach the malformed count.
                case 'L' when labelsOnly:
                    break;

                case 'L' when TryParseLine(record, out var line):
                    lines.Add(line);
                    bounds = bounds.Add(line.From).Add(line.To);
                    break;

                case 'P' when TryParseLabel(record, out var label):
                    labels.Add(label);
                    bounds = bounds.Add(label.At);
                    break;

                default:
                    malformed++;
                    break;
            }
        }

        return new MapLayer(layerIndex, lines, labels, bounds, malformed);
    }

    /// <summary>
    /// Splits the text into records: on newlines, and additionally wherever a
    /// record type follows a digit with no separator — the run-together case
    /// described on the class. Requiring a digit before the <c>L</c>/<c>P</c>
    /// is what keeps this from cutting a label like <c>to_Plane_of_Sky</c> in
    /// half, since a label character is never a digit at the split point.
    /// </summary>
    private static IEnumerable<string> Records(string text)
    {
        var start = 0;

        for (var i = 0; i <= text.Length; i++)
        {
            var atEnd = i == text.Length;
            var boundary = atEnd || text[i] is '\n' or '\r';

            if (!boundary && i > start + 1 && text[i] is 'L' or 'P'
                && i + 1 < text.Length && text[i + 1] == ' '
                && char.IsAsciiDigit(text[i - 1]))
            {
                var run = text[start..i].Trim();
                if (run.Length > 0)
                {
                    yield return run;
                }

                start = i;
                continue;
            }

            if (!boundary)
            {
                continue;
            }

            var record = text[start..i].Trim();
            if (record.Length > 0)
            {
                yield return record;
            }

            start = i + 1;
        }
    }

    private static bool TryParseLine(string record, out MapLine line)
    {
        line = default!;

        Span<float> f = stackalloc float[9];
        if (!TryParseFields(record.AsSpan(1), f, out _))
        {
            return false;
        }

        line = new MapLine(
            new MapPoint(f[0], f[1], f[2]),
            new MapPoint(f[3], f[4], f[5]),
            new MapColor(Channel(f[6]), Channel(f[7]), Channel(f[8])));
        return true;
    }

    private static bool TryParseLabel(string record, out MapLabel label)
    {
        label = default!;

        Span<float> f = stackalloc float[7];
        if (!TryParseFields(record.AsSpan(1), f, out var consumed))
        {
            return false;
        }

        // Everything past the seventh comma is the label, commas and all.
        var rest = record.AsSpan(1 + consumed).Trim();

        label = new MapLabel(
            new MapPoint(f[0], f[1], f[2]),
            new MapColor(Channel(f[3]), Channel(f[4]), Channel(f[5])),
            (int)f[6],
            rest.ToString().Replace('_', ' ').Trim());
        return true;
    }

    /// <summary>
    /// Fills <paramref name="fields"/> from a comma-separated span, reporting
    /// how much of the span was consumed including the trailing comma. A record
    /// with more fields than asked for is fine — that is how the label is
    /// found — but one with fewer is malformed.
    /// </summary>
    private static bool TryParseFields(ReadOnlySpan<char> span, Span<float> fields, out int consumed)
    {
        consumed = 0;

        for (var i = 0; i < fields.Length; i++)
        {
            var comma = span.IndexOf(',');
            var last = i == fields.Length - 1;

            // Only the final field may run to the end of the record: an L
            // record ends there, a P record hands the remainder to the label.
            if (comma < 0 && !last)
            {
                return false;
            }

            var piece = comma < 0 ? span : span[..comma];
            if (!float.TryParse(piece.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out fields[i]))
            {
                return false;
            }

            if (comma < 0)
            {
                consumed = span.Length;
                return true;
            }

            consumed += comma + 1;
            span = span[(comma + 1)..];
        }

        return true;
    }

    /// <summary>
    /// Clamps a colour channel. The corpus is hand-edited and does occasionally
    /// carry an out-of-range or fractional channel; the client shows those maps,
    /// so refusing them here would lose a zone over a cosmetic detail.
    /// </summary>
    private static byte Channel(float value) =>
        value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)value;
}
