using System.Text.Json;
using EQDeeps.Core.Events;
using EQDeeps.Core.Parsing;
using Xunit;

namespace EQDeeps.Core.Tests;

public class ModifierParserTests
{
    public static TheoryData<int> Cases()
    {
        var data = new TheoryData<int>();
        var count = Load().GetArrayLength();
        for (var i = 0; i < count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    private static JsonElement Load() =>
        JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "modifiers.json"))).RootElement;

    [Theory]
    [MemberData(nameof(Cases))]
    public void ModifierFixture(int index)
    {
        var fixture = Load()[index];
        var text = fixture.GetProperty("text").GetString()!;
        var expected = FixtureTests.ExpectedModifiers(fixture.GetProperty("expect"));
        Assert.Equal(expected, ModifierParser.Parse(text));
    }

    [Fact]
    public void NullTextParsesToNone()
    {
        Assert.Equal(HitModifiers.None, ModifierParser.Parse(null));
    }

    [Fact]
    public void UnknownTokenReportsNotFullyRecognized()
    {
        Assert.False(ModifierParser.TryParse("Earthquake", out var mods));
        Assert.Equal(HitModifiers.None, mods);
    }
}
