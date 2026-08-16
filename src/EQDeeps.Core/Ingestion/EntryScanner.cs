using System.Text;
using EQDeeps.Core.Parsing;

namespace EQDeeps.Core.Ingestion;

/// <summary>
/// Turns raw byte chunks into timestamped <see cref="LogEntry"/> values.
/// Incomplete trailing lines are carried across chunks and never emitted —
/// a live read can catch the game mid-write. Consecutive lines usually share a
/// timestamp, so the previous line's 27-char prefix and parsed time are memoized
/// and reused on match. Encoding is treated as single-byte (Latin-1): EQ logs are
/// ASCII/ANSI in practice and stray bytes must round-trip harmlessly.
/// </summary>
public sealed class EntryScanner
{
    private readonly int _maxLineLength;
    private byte[] _carry;
    private int _carryLength;
    private bool _discardingOverlongLine;
    private string? _previousPrefix;
    private DateTime _previousTimestamp;

    /// <summary>Non-empty lines that produced no entry (no valid timestamp prefix).</summary>
    public long MalformedLines { get; private set; }

    /// <summary>Lines dropped for exceeding the length bound.</summary>
    public long OverlongLinesDropped { get; private set; }

    /// <summary>
    /// Bytes appended since the last newline — the unfinished line being held
    /// back, whether carried or (past the length bound) being discarded. What
    /// the caller subtracts from its read position to name the offset just
    /// past the last complete line: the one place a reader could reopen the
    /// file and continue without duplicating or losing an entry.
    /// </summary>
    public long PendingBytes { get; private set; }

    public EntryScanner(int maxLineLength = 64 * 1024)
    {
        _maxLineLength = maxLineLength;
        _carry = new byte[1024];
    }

    /// <summary>Forget carried bytes and memo — call after truncation/rotation reopens.</summary>
    public void Reset()
    {
        _carryLength = 0;
        _discardingOverlongLine = false;
        _previousPrefix = null;
        PendingBytes = 0;
    }

    public void Append(ReadOnlySpan<byte> data, List<LogEntry> output)
    {
        while (!data.IsEmpty)
        {
            var newline = data.IndexOf((byte)'\n');
            if (newline < 0)
            {
                // Counted here and only here: Carry is also how a held line
                // is completed, and that completion is not pending — it is
                // about to be processed. Counting inside Carry once left the
                // completion's length behind in a chunk that ended right after
                // it, and a resume offset that far short of the line start.
                PendingBytes += data.Length;
                Carry(data);
                return;
            }

            var line = data[..newline];
            data = data[(newline + 1)..];
            PendingBytes = 0;

            if (_discardingOverlongLine)
            {
                _discardingOverlongLine = false;
                _carryLength = 0;
                continue;
            }

            if (_carryLength > 0)
            {
                Carry(line);
                if (_discardingOverlongLine)
                {
                    _discardingOverlongLine = false;
                    _carryLength = 0;
                    continue;
                }

                ProcessLineBytes(_carry.AsSpan(0, _carryLength), output);
                _carryLength = 0;
            }
            else
            {
                ProcessLineBytes(line, output);
            }
        }
    }

    private void Carry(ReadOnlySpan<byte> data)
    {
        if (_discardingOverlongLine)
        {
            return;
        }

        if (_carryLength + data.Length > _maxLineLength)
        {
            _discardingOverlongLine = true;
            OverlongLinesDropped++;
            return;
        }

        if (_carryLength + data.Length > _carry.Length)
        {
            Array.Resize(ref _carry, Math.Max(_carry.Length * 2, _carryLength + data.Length));
        }

        data.CopyTo(_carry.AsSpan(_carryLength));
        _carryLength += data.Length;
    }

    private void ProcessLineBytes(ReadOnlySpan<byte> line, List<LogEntry> output)
    {
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        if (line.Length == 0)
        {
            return;
        }

        if (line.Length < LogTimestamp.PrefixLength + 1)
        {
            MalformedLines++;
            return;
        }

        ProcessLine(Encoding.Latin1.GetString(line), output);
    }

    /// <summary>Processes one complete decoded line (exposed for tests).</summary>
    public void ProcessLine(string line, List<LogEntry> output)
    {
        // Fast path: same timestamp prefix as the previous line, and no '[' in the
        // body that could hide a glitched second entry.
        if (_previousPrefix is not null &&
            line.Length > LogTimestamp.PrefixLength &&
            line.AsSpan(0, LogTimestamp.PrefixLength).SequenceEqual(_previousPrefix) &&
            line.AsSpan(LogTimestamp.PrefixLength).IndexOf('[') < 0)
        {
            output.Add(new LogEntry(_previousTimestamp, line[LogTimestamp.PrefixLength..]));
            return;
        }

        var before = output.Count;
        LogLineSplitter.Split(line, output);
        if (output.Count == before)
        {
            MalformedLines++;
            return;
        }

        _previousPrefix = line[..LogTimestamp.PrefixLength];
        _previousTimestamp = output[before].Timestamp;
    }
}
