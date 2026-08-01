namespace EQDeeps.Core.Parsing;

/// <summary>One timestamped message extracted from a physical log line.</summary>
public readonly record struct LogEntry(DateTime Timestamp, string Action);

/// <summary>
/// Splits a physical log line into timestamped entries. Normally one line is one
/// entry, but the game occasionally concatenates a second "[timestamp] ..." entry
/// onto the same physical line; we probe for a strictly valid embedded timestamp
/// and split. Strict validation in <see cref="LogTimestamp"/> keeps chat text like
/// "[60 High Priest]" or "[queued]," from triggering false splits.
/// </summary>
public static class LogLineSplitter
{
    /// <summary>
    /// Appends the entries found in <paramref name="rawLine"/> to <paramref name="output"/>.
    /// Lines without a valid timestamp prefix (partial writes, junk) yield nothing.
    /// </summary>
    public static void Split(string rawLine, List<LogEntry> output)
    {
        var start = 0;
        while (start < rawLine.Length)
        {
            var span = rawLine.AsSpan(start);
            if (!LogTimestamp.TryParse(span, out var timestamp))
            {
                return;
            }

            var actionStart = start + LogTimestamp.PrefixLength;
            var actionEnd = FindEmbeddedTimestamp(rawLine, actionStart);
            output.Add(new LogEntry(timestamp, rawLine[actionStart..actionEnd]));
            start = actionEnd;
        }
    }

    private static int FindEmbeddedTimestamp(string line, int from)
    {
        var i = from;
        while ((i = line.IndexOf('[', i)) >= 0)
        {
            if (LogTimestamp.TryParse(line.AsSpan(i), out _))
            {
                return i;
            }

            i++;
        }

        return line.Length;
    }
}
