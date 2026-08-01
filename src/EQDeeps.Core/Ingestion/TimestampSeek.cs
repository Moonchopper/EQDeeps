using EQDeeps.Core.Parsing;

namespace EQDeeps.Core.Ingestion;

/// <summary>
/// Finds the byte offset of the first line whose timestamp is at or after a
/// target time, without reading the whole file: log timestamps are ordered
/// (modulo DST regressions), so binary-search byte offsets, align each probe to
/// the next line start, and compare its timestamp. The lower bound only ever
/// advances to line starts proven earlier than the target, so the final linear
/// scan from it is exact even when probes land on unparseable bytes; DST
/// regressions can only make the result conservatively early or slightly late,
/// never crash.
/// </summary>
public static class TimestampSeek
{
    private const int ProbeWindow = 64 * 1024;

    /// <summary>
    /// Returns the offset to start reading from, or the file length when every
    /// line is older than <paramref name="target"/>. The stream position is
    /// left undefined; callers seek to the result.
    /// </summary>
    public static long FindStart(Stream stream, DateTime target)
    {
        var length = stream.Length;
        if (length == 0)
        {
            return 0;
        }

        var buffer = new byte[ProbeWindow];

        // If the very first line is already inside the window, no search needed.
        if (TryParseLineAt(stream, 0, buffer, out var firstTimestamp) && firstTimestamp >= target)
        {
            return 0;
        }

        long lo = 0;
        var hi = length;
        while (hi - lo > ProbeWindow)
        {
            var mid = lo + (hi - lo) / 2;
            if (TryProbeNextLine(stream, mid, buffer, out var lineStart, out var timestamp) && lineStart < hi)
            {
                if (timestamp < target)
                {
                    lo = lineStart;
                }
                else
                {
                    hi = lineStart;
                }
            }
            else
            {
                hi = mid;
            }
        }

        return LinearScan(stream, lo, target, length);
    }

    /// <summary>Parses the timestamp of the line starting exactly at <paramref name="offset"/>.</summary>
    private static bool TryParseLineAt(Stream stream, long offset, byte[] buffer, out DateTime timestamp)
    {
        timestamp = default;
        stream.Seek(offset, SeekOrigin.Begin);
        var read = FillBuffer(stream, buffer, LogTimestamp.PrefixLength);
        return TryParsePrefix(buffer.AsSpan(0, read), out timestamp);
    }

    /// <summary>
    /// Seeks to <paramref name="offset"/>, skips to the next line start, and
    /// parses its timestamp; skips over unparseable lines (glitches, junk)
    /// within the probe window.
    /// </summary>
    private static bool TryProbeNextLine(
        Stream stream, long offset, byte[] buffer, out long lineStart, out DateTime timestamp)
    {
        lineStart = 0;
        timestamp = default;
        stream.Seek(offset, SeekOrigin.Begin);
        var read = FillBuffer(stream, buffer, buffer.Length);
        var span = buffer.AsSpan(0, read);

        var position = 0;
        while (true)
        {
            var newline = span[position..].IndexOf((byte)'\n');
            if (newline < 0)
            {
                return false;
            }

            position += newline + 1;
            if (position >= span.Length)
            {
                return false;
            }

            if (TryParsePrefix(span[position..], out timestamp))
            {
                lineStart = offset + position;
                return true;
            }
        }
    }

    private static long LinearScan(Stream stream, long start, DateTime target, long length)
    {
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[ProbeWindow];
        var bufferStart = start;
        var filled = 0;
        var lineStart = 0;

        while (true)
        {
            var read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read == 0)
            {
                return length;
            }

            filled += read;
            var span = buffer.AsSpan(0, filled);

            while (true)
            {
                if (TryParsePrefix(span[lineStart..], out var timestamp) && timestamp >= target)
                {
                    return bufferStart + lineStart;
                }

                var newline = span[lineStart..].IndexOf((byte)'\n');
                if (newline < 0)
                {
                    break;
                }

                lineStart += newline + 1;
            }

            // Shift the partial tail line to the front and refill.
            var tail = filled - lineStart;
            if (tail >= buffer.Length)
            {
                // A "line" longer than the window with no timestamp — skip it.
                bufferStart += filled;
                filled = 0;
                lineStart = 0;
                continue;
            }

            span[lineStart..].CopyTo(buffer);
            bufferStart += lineStart;
            filled = tail;
            lineStart = 0;
        }
    }

    private static bool TryParsePrefix(ReadOnlySpan<byte> line, out DateTime timestamp)
    {
        timestamp = default;
        if (line.Length < LogTimestamp.PrefixLength)
        {
            return false;
        }

        Span<char> chars = stackalloc char[LogTimestamp.PrefixLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)line[i];
        }

        return LogTimestamp.TryParse(chars, out timestamp);
    }

    private static int FillBuffer(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, total, count - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
