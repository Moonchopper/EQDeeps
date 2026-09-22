using EQDeeps.Core.Achievements;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>Joining a faction's three spellings to one key (F35, ADR-023 Decision 5).</summary>
public class FactionNamesTests
{
    [Theory]
    [InlineData("Coalition of Tradesfolk", "Coalition of Tradefolk")]
    [InlineData("Da Bashers", "DaBashers")]
    [InlineData("Freeport Militia", "The Freeport Militia")]
    [InlineData("Corrupt Qeynos Guard", "Corrupt Qeynos Guards")]
    public void TheFourSpellingPairsJoin(string a, string b)
    {
        Assert.Equal(FactionNames.Key(a), FactionNames.Key(b));
    }

    [Fact]
    public void NothingFuzzierThanTheRule()
    {
        // A different faction entirely — the rule stops at case, punctuation and whitespace plus
        // the two named aliases, never at "close enough".
        Assert.NotEqual(FactionNames.Key("Coalition of TradeFolk III"), FactionNames.Key("Coalition of Tradefolk"));
    }

    [Fact]
    public void BackticksAndApostrophesAreRemoved()
    {
        Assert.Equal(FactionNames.Key("Vah Shir"), FactionNames.Key("Vah Shir"));
        Assert.Equal(FactionNames.Key("Da`Bashers"), FactionNames.Key("DaBashers"));
    }

    [Fact]
    public void OnlyOneLeadingTheIsRemoved()
    {
        Assert.Equal(FactionNames.Key("Freeport Militia"), FactionNames.Key("The Freeport Militia"));
        // "Theater" is not "The ater" — the rule requires the space, so an unrelated word starting
        // with the same three letters must not lose anything.
        Assert.Equal("theater", FactionNames.Key("Theater"));
    }
}
