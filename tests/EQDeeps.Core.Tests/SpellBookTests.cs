using EQDeeps.Core.Events;
using EQDeeps.Core.Parsing;
using EQDeeps.Core.Spells;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// Resolving the per-spell emotes against the player's own spell files.
///
/// <para>The fixtures are the real shapes, trimmed: a spell whose emote is its
/// own, two that share one (every rank of a heal says the same thing), and the
/// possessive form the client uses for someone else. The whole point is that a
/// shared emote must not be reported as a spell.</para>
/// </summary>
public class SpellBookTests
{
    // id^name^… — only the first two columns are read of the file's 173.
    private const string SpellsUs = """
        278^Spirit of Wolf^0^^1500^0
        13^Minor Healing^0^^1000^0
        14^Light Healing^0^^1000^0
        1447^Ignite Blood^0^^0^0
        900^Shimmering Aura^0^^0^0
        """;

    // #SPELLINDEX^CASTERMETXT^CASTEROTHERTXT^CASTEDMETXT^CASTEDOTHERTXT^SPELLGONE^
    private const string SpellsUsStr = """
        #SPELLINDEX^CASTERMETXT^CASTEROTHERTXT^CASTEDMETXT^CASTEDOTHERTXT^SPELLGONE^
        278^^^You feel the spirit of wolf enter you.^begins to run like the wind.^You feel slower.^
        13^^^Your wounds begin to heal.^looks healthier.^^
        14^^^Your wounds begin to heal.^looks healthier.^^
        1447^^^Your blood ignites.^'s blood ignites.^Your blood cools.^
        900^^^^ shimmers.^^
        """;

    private static SpellBook Book() => SpellBook.Build(SpellsUs, SpellsUsStr);

    [Fact]
    public void AnEmoteOfItsOwnNamesItsSpell()
    {
        var book = Book();
        Assert.True(book.TryLandsOnYou("You feel the spirit of wolf enter you.", out var match));
        Assert.Equal("Spirit of Wolf", match.Spell);
        Assert.Equal(1, match.Candidates);
        Assert.False(match.IsAmbiguous);
    }

    [Fact]
    public void AnEmoteSeveralSpellsShareNamesNone()
    {
        var book = Book();
        Assert.True(book.TryLandsOnYou("Your wounds begin to heal.", out var match));
        // Minor Healing and Light Healing both say it, so neither is claimed.
        Assert.Null(match.Spell);
        Assert.Equal(2, match.Candidates);
        Assert.True(match.IsAmbiguous);
    }

    [Fact]
    public void FadesAndUnknownLinesBehave()
    {
        var book = Book();
        Assert.True(book.TryFade("Your blood cools.", out var fade));
        Assert.Equal("Ignite Blood", fade.Spell);
        Assert.False(book.TryLandsOnYou("Soandso hits you for 12 points of damage.", out _));
        Assert.False(book.TryFade("Nothing says this.", out _));
    }

    [Fact]
    public void ALandsOnOtherLineIsSplitIntoNameAndSpell()
    {
        var book = Book();

        // The possessive form has no separator to split on; the fragment is
        // recognised from the end and the name is what precedes it.
        Assert.True(book.TryLandsOnOther("Soandso's blood ignites.", out var target, out var match));
        Assert.Equal("Soandso", target);
        Assert.Equal("Ignite Blood", match.Spell);

        // The plain form, where the fragment follows a space.
        Assert.True(book.TryLandsOnOther("Raider02 begins to run like the wind.", out var runner, out var sow));
        Assert.Equal("Raider02", runner);
        Assert.Equal("Spirit of Wolf", sow.Spell);

        // A name of several words still works, since the split is found from the tail.
        Assert.True(book.TryLandsOnOther("A froglok ton knight looks healthier.", out var mob, out var heal));
        Assert.Equal("A froglok ton knight", mob);
        Assert.Null(heal.Spell);
        Assert.Equal(2, heal.Candidates);

        Assert.False(book.TryLandsOnOther("Soandso says something else entirely.", out _, out _));

        // A split that leaves a bare article has landed mid-sentence, not on a
        // target: "The barking fades." is not Barking landing on "The".
        Assert.False(book.TryLandsOnOther("The looks healthier.", out _, out _));
    }

    [Fact]
    public void AnEmptyBookMatchesNothingAndParsingCarriesOn()
    {
        Assert.True(SpellBook.Empty.IsEmpty);
        Assert.False(SpellBook.Empty.TryLandsOnYou("You feel the spirit of wolf enter you.", out _));
        // A file that is missing or unreadable yields the same empty book.
        Assert.True(SpellBook.Build("", "").IsEmpty);
        Assert.True(SpellBook.Build("garbage", "more garbage").IsEmpty);
    }

    [Fact]
    public void TheParserEmitsLandedEventsOnlyWhenItHasTheFiles()
    {
        var without = new ParserOptions("Kizant");
        var with = new ParserOptions("Kizant") { Spells = Book() };
        var parser = new LogEventParser(with);

        var landed = Assert.IsType<LandedEvent>(parser.Parse("You feel the spirit of wolf enter you."));
        Assert.Equal("Kizant", landed.Target);
        Assert.Equal("Spirit of Wolf", landed.Spell);

        var shared = Assert.IsType<LandedEvent>(parser.Parse("Your wounds begin to heal."));
        Assert.Null(shared.Spell);
        Assert.Equal("Your wounds begin to heal.", shared.Emote);
        Assert.Equal(2, shared.Candidates);

        var onOther = Assert.IsType<LandedEvent>(parser.Parse("Soandso's blood ignites."));
        Assert.Equal("Soandso", onOther.Target);
        Assert.Equal("Ignite Blood", onOther.Spell);

        // An emote fade that names one spell is the same event the named
        // "worn off" line produces.
        var fade = Assert.IsType<WearOffEvent>(parser.Parse("Your blood cools."));
        Assert.Equal("Ignite Blood", fade.Spell);

        // Without the files, these lines are simply not ours.
        var blind = new LogEventParser(without);
        Assert.Null(blind.Parse("You feel the spirit of wolf enter you."));
        Assert.Null(blind.Parse("Soandso's blood ignites."));
    }

    [Fact]
    public void RealGrammarsStillWinOverEmotes()
    {
        // The emote table is consulted last, so a line with a shape of its own
        // keeps its meaning even if some spell's text happens to resemble it.
        var parser = new LogEventParser(new ParserOptions("Kizant") { Spells = Book() });
        Assert.IsType<WearOffEvent>(parser.Parse("Your Spirit of Wolf spell has worn off."));
        Assert.IsType<CastEvent>(parser.Parse("You begin casting Minor Healing."));
        Assert.IsType<ChatEvent>(parser.Parse("Soandso says, 'Your wounds begin to heal.'"));
    }
}
