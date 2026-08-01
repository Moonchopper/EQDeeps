using EQDeeps.Core.Parsing;
using Xunit;

namespace EQDeeps.Core.Tests;

public class LogLineSplitterTests
{
    [Fact]
    public void ParsesTimestampPrefix()
    {
        Assert.True(LogTimestamp.TryParse("[Sun Oct 08 20:07:10 2023] Test says, 'hello'", out var ts));
        Assert.Equal(new DateTime(2023, 10, 8, 20, 7, 10), ts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("[Sun Oct 08 20:07:10 20xx] bad year")]
    [InlineData("[Xyz Oct 08 20:07:10 2023] bad day name")]
    [InlineData("[Sun Zzz 08 20:07:10 2023] bad month")]
    [InlineData("[Sun Oct 32 20:07:10 2023] bad day")]
    [InlineData("[Sun Oct 08 24:07:10 2023] bad hour")]
    [InlineData("Sun Oct 08 20:07:10 2023] no bracket")]
    public void RejectsMalformedTimestamps(string line)
    {
        Assert.False(LogTimestamp.TryParse(line, out _));
    }

    [Fact]
    public void SplitsNormalLine()
    {
        var entries = new List<LogEntry>();
        LogLineSplitter.Split("[Sun Oct 08 20:07:10 2023] An ice giant died.", entries);

        var entry = Assert.Single(entries);
        Assert.Equal(new DateTime(2023, 10, 8, 20, 7, 10), entry.Timestamp);
        Assert.Equal("An ice giant died.", entry.Action);
    }

    [Fact]
    public void SplitsGlitchedDoubleEntryLine()
    {
        var entries = new List<LogEntry>();
        LogLineSplitter.Split(
            "[Sun Oct 08 20:07:10 2023] Test says, 'hello'[Sun Oct 08 20:07:11 2023] An ice giant died.",
            entries);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Test says, 'hello'", entries[0].Action);
        Assert.Equal(new DateTime(2023, 10, 8, 20, 7, 11), entries[1].Timestamp);
        Assert.Equal("An ice giant died.", entries[1].Action);
    }

    [Fact]
    public void DoesNotSplitOnBracketedChatText()
    {
        var entries = new List<LogEntry>();
        LogLineSplitter.Split(
            "[Sun Oct 08 20:07:10 2023] [60 High Priest] Soandso (High Elf) <Guild Name>",
            entries);

        var entry = Assert.Single(entries);
        Assert.Equal("[60 High Priest] Soandso (High Elf) <Guild Name>", entry.Action);
    }

    [Fact]
    public void PartialLineWithoutTimestampYieldsNothing()
    {
        var entries = new List<LogEntry>();
        LogLineSplitter.Split("ial line from a mid-write read", entries);
        Assert.Empty(entries);
    }
}
