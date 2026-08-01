using System.Text;
using EQDeeps.Core.Ingestion;
using EQDeeps.Core.Parsing;
using Xunit;

namespace EQDeeps.Core.Tests;

public class EntryScannerTests
{
    private static readonly string L1 = "[Sun Oct 08 20:07:10 2023] Test says, 'hello'";
    private static readonly string L2 = "[Sun Oct 08 20:07:10 2023] An ice giant died.";
    private static readonly string L3 = "[Sun Oct 08 20:07:11 2023] Kzerk has been slain by Renewingx!";

    private static List<LogEntry> Scan(params string[] chunks)
    {
        var scanner = new EntryScanner();
        var output = new List<LogEntry>();
        foreach (var chunk in chunks)
        {
            scanner.Append(Encoding.Latin1.GetBytes(chunk), output);
        }

        return output;
    }

    [Fact]
    public void SplitsLinesAcrossChunkBoundaries()
    {
        var text = L1 + "\r\n" + L2 + "\r\n" + L3 + "\r\n";
        for (var split = 1; split < text.Length - 1; split += 7)
        {
            var entries = Scan(text[..split], text[split..]);
            Assert.Equal(3, entries.Count);
            Assert.Equal("Test says, 'hello'", entries[0].Action);
            Assert.Equal("An ice giant died.", entries[1].Action);
            Assert.Equal(new DateTime(2023, 10, 8, 20, 7, 11), entries[2].Timestamp);
        }
    }

    [Fact]
    public void MemoizedTimestampPathMatchesFullParse()
    {
        // L1 and L2 share a timestamp — the second line takes the memo fast path.
        var entries = Scan(L1 + "\n" + L2 + "\n");
        Assert.Equal(2, entries.Count);
        Assert.Equal(entries[0].Timestamp, entries[1].Timestamp);
        Assert.Equal("An ice giant died.", entries[1].Action);
    }

    [Fact]
    public void TrailingBytesWithoutNewlineAreNotEmitted()
    {
        var entries = Scan(L1 + "\n" + L2[..20]);
        var entry = Assert.Single(entries);
        Assert.Equal("Test says, 'hello'", entry.Action);
    }

    [Fact]
    public void GlitchedDoubleEntrySplitsEvenOnMemoPrefix()
    {
        // Same prefix as previous line, but the body hides a second entry — the
        // '[' probe must force the full-parse path.
        var glitched = "[Sun Oct 08 20:07:10 2023] Test says, 'x'[Sun Oct 08 20:07:12 2023] An ice giant died.";
        var entries = Scan(L1 + "\n" + glitched + "\n");
        Assert.Equal(3, entries.Count);
        Assert.Equal("Test says, 'x'", entries[1].Action);
        Assert.Equal(new DateTime(2023, 10, 8, 20, 7, 12), entries[2].Timestamp);
    }

    [Fact]
    public void MalformedLinesAreCountedNotEmitted()
    {
        var scanner = new EntryScanner();
        var output = new List<LogEntry>();
        scanner.Append(Encoding.Latin1.GetBytes("no timestamp here at all, but a long line\n" + L1 + "\n"), output);
        Assert.Single(output);
        Assert.Equal(1, scanner.MalformedLines);
    }

    [Fact]
    public void OverlongLinesAreDroppedAndCounted()
    {
        var scanner = new EntryScanner(maxLineLength: 128);
        var output = new List<LogEntry>();
        scanner.Append(Encoding.Latin1.GetBytes(new string('x', 100)), output);
        scanner.Append(Encoding.Latin1.GetBytes(new string('x', 100)), output);
        scanner.Append(Encoding.Latin1.GetBytes("\n" + L1 + "\n"), output);

        Assert.Single(output);
        Assert.Equal(1, scanner.OverlongLinesDropped);
        Assert.Equal("Test says, 'hello'", output[0].Action);
    }

    [Fact]
    public void ResetForgetsCarriedPartialLine()
    {
        var scanner = new EntryScanner();
        var output = new List<LogEntry>();
        scanner.Append(Encoding.Latin1.GetBytes(L1[..30]), output);
        scanner.Reset();
        scanner.Append(Encoding.Latin1.GetBytes(L2 + "\n"), output);

        var entry = Assert.Single(output);
        Assert.Equal("An ice giant died.", entry.Action);
    }
}
