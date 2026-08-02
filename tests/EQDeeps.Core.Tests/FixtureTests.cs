using System.Text.Json;
using EQDeeps.Core.Events;
using EQDeeps.Core.Parsing;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// Data-driven corpus tests. Each fixture is a real log line plus the expected
/// typed event; expected values were harvested from EQLogParser's parser tests
/// (see NOTICE) and the domain docs. Only properties present in the JSON are
/// asserted, so fixtures state exactly what the source guaranteed.
/// </summary>
public class FixtureTests
{
    private const string PlayerName = "TestPlayer";

    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (var file in FixtureFiles())
        {
            var count = JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetArrayLength();
            for (var i = 0; i < count; i++)
            {
                data.Add(Path.GetFileName(file), i);
            }
        }

        return data;
    }

    private static IEnumerable<string> FixtureFiles() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.json")
            .Where(f => Path.GetFileName(f) != "modifiers.json")
            .OrderBy(f => f, StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Fixture(string file, int index)
    {
        var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));
        var fixture = doc.RootElement[index];

        var emu = fixture.TryGetProperty("emu", out var emuProp) && emuProp.GetBoolean();
        var parser = new LogEventParser(new ParserOptions(PlayerName, emu));

        if (fixture.TryGetProperty("setup", out var setup))
        {
            foreach (var prior in setup.EnumerateArray())
            {
                parser.Parse(prior.GetString()!);
            }
        }

        var line = fixture.GetProperty("line").GetString()!;
        var evt = parser.Parse(line);
        var expect = fixture.GetProperty("expect");
        var context = $"{file}[{index}]: {line}";

        var type = expect.GetProperty("type").GetString();
        if (type == "none")
        {
            Assert.True(evt is null, $"{context} — expected no event but got {evt}");
            return;
        }

        Assert.True(evt is not null, $"{context} — expected {type} but got null");
        switch (type)
        {
            case "damage":
                AssertDamage(expect, Assert.IsType<DamageEvent>(evt), context);
                break;
            case "heal":
                AssertHeal(expect, Assert.IsType<HealEvent>(evt), context);
                break;
            case "death":
                var death = Assert.IsType<DeathEvent>(evt);
                AssertString(expect, "victim", death.Victim, context);
                AssertString(expect, "killer", death.Killer, context);
                break;
            case "cast":
                var cast = Assert.IsType<CastEvent>(evt);
                AssertString(expect, "caster", cast.Caster, context);
                AssertString(expect, "spell", cast.Spell, context);
                AssertEnum<CastKind>(expect, "kind", cast.Kind, context);
                AssertBool(expect, "song", cast.Song, context);
                break;
            case "wearOff":
                var wearOff = Assert.IsType<WearOffEvent>(evt);
                AssertString(expect, "spell", wearOff.Spell, context);
                AssertString(expect, "target", wearOff.Target, context);
                break;
            case "ability":
                var ability = Assert.IsType<AbilityEvent>(evt);
                AssertString(expect, "user", ability.User, context);
                AssertString(expect, "ability", ability.Ability, context);
                break;
            case "chat":
                var chat = Assert.IsType<ChatEvent>(evt);
                AssertEnum<ChatChannel>(expect, "channel", chat.Channel, context);
                AssertString(expect, "customChannel", chat.CustomChannel, context);
                AssertString(expect, "sender", chat.Sender, context);
                AssertString(expect, "receiver", chat.Receiver, context);
                AssertString(expect, "text", chat.Text, context);
                break;
            case "taunt":
                var taunt = Assert.IsType<TauntEvent>(evt);
                AssertString(expect, "taunter", taunt.Taunter, context);
                AssertString(expect, "target", taunt.Target, context);
                AssertBool(expect, "success", taunt.Success, context);
                AssertBool(expect, "improved", taunt.Improved, context);
                break;
            case "zone":
                AssertString(expect, "zoneName", Assert.IsType<ZoneEvent>(evt).ZoneName, context);
                break;
            case "resist":
                var resist = Assert.IsType<ResistEvent>(evt);
                AssertString(expect, "caster", resist.Caster, context);
                AssertString(expect, "resister", resist.Resister, context);
                AssertString(expect, "spell", resist.Spell, context);
                break;
            case "membership":
                var membership = Assert.IsType<MembershipEvent>(evt);
                AssertString(expect, "player", membership.Player, context);
                AssertBool(expect, "raid", membership.Raid, context);
                AssertBool(expect, "joined", membership.Joined, context);
                break;
            case "experience":
                var xp = Assert.IsType<ExperienceEvent>(evt);
                AssertBool(expect, "party", xp.Party, context);
                AssertBool(expect, "aaPoint", xp.AaPoint, context);
                if (expect.TryGetProperty("percent", out var percent))
                {
                    var expectedPercent = percent.ValueKind == JsonValueKind.Null
                        ? (double?)null
                        : percent.GetDouble();
                    Assert.True(expectedPercent == xp.Percent,
                        $"{context} — percent expected {expectedPercent} got {xp.Percent}");
                }

                if (expect.TryGetProperty("aaTotal", out var aaTotal))
                {
                    var expectedTotal = aaTotal.ValueKind == JsonValueKind.Null
                        ? (int?)null
                        : aaTotal.GetInt32();
                    Assert.True(expectedTotal == xp.AaTotal,
                        $"{context} — aaTotal expected {expectedTotal} got {xp.AaTotal}");
                }

                break;
            case "faction":
                var faction = Assert.IsType<FactionEvent>(evt);
                AssertString(expect, "faction", faction.Faction, context);
                AssertBool(expect, "better", faction.Better, context);
                AssertBool(expect, "capped", faction.Capped, context);
                if (expect.TryGetProperty("delta", out var delta))
                {
                    var expectedDelta = delta.ValueKind == JsonValueKind.Null
                        ? (int?)null
                        : delta.GetInt32();
                    Assert.True(expectedDelta == faction.Delta,
                        $"{context} — delta expected {expectedDelta} got {faction.Delta}");
                }

                break;
            case "loot":
                var loot = Assert.IsType<LootEvent>(evt);
                AssertString(expect, "looter", loot.Looter, context);
                AssertString(expect, "item", loot.Item, context);
                AssertString(expect, "source", loot.Source, context);
                if (expect.TryGetProperty("copper", out var copper))
                {
                    var expectedCopper = copper.ValueKind == JsonValueKind.Null
                        ? (long?)null
                        : copper.GetInt64();
                    Assert.True(expectedCopper == loot.Copper,
                        $"{context} — copper expected {expectedCopper} got {loot.Copper}");
                }

                if (expect.TryGetProperty("quantity", out var quantity))
                {
                    Assert.True(quantity.GetInt32() == loot.Quantity,
                        $"{context} — quantity expected {quantity.GetInt32()} got {loot.Quantity}");
                }

                break;
            case "consider":
                var consider = Assert.IsType<ConsiderEvent>(evt);
                AssertString(expect, "target", consider.Target, context);
                AssertString(expect, "attitude", consider.Attitude, context);
                if (expect.TryGetProperty("level", out var conLevel))
                {
                    var expectedConLevel = conLevel.ValueKind == JsonValueKind.Null
                        ? (int?)null
                        : conLevel.GetInt32();
                    Assert.True(expectedConLevel == consider.Level,
                        $"{context} — level expected {expectedConLevel} got {consider.Level}");
                }

                break;
            case "who":
                var who = Assert.IsType<WhoEvent>(evt);
                AssertString(expect, "player", who.Player, context);
                AssertString(expect, "classText", who.ClassText, context);
                if (expect.TryGetProperty("level", out var level))
                {
                    var expectedLevel = level.ValueKind == JsonValueKind.Null ? (int?)null : level.GetInt32();
                    Assert.True(expectedLevel == who.Level, $"{context} — level expected {expectedLevel} got {who.Level}");
                }

                break;
            default:
                Assert.Fail($"{context} — unknown expectation type '{type}'");
                break;
        }
    }

    private static void AssertDamage(JsonElement expect, DamageEvent evt, string context)
    {
        AssertString(expect, "attacker", evt.Attacker, context);
        AssertString(expect, "defender", evt.Defender, context);
        AssertString(expect, "subType", evt.SubType, context);
        AssertString(expect, "attackerOwner", evt.AttackerOwner, context);
        AssertString(expect, "defenderOwner", evt.DefenderOwner, context);
        AssertString(expect, "school", evt.School, context);
        AssertEnum<DamageKind>(expect, "kind", evt.Kind, context);
        AssertBool(expect, "attackerIsSpell", evt.AttackerIsSpell, context);

        if (expect.TryGetProperty("amount", out var amount))
        {
            Assert.True(amount.GetUInt32() == evt.Amount,
                $"{context} — amount expected {amount.GetUInt32()} got {evt.Amount}");
        }

        if (expect.TryGetProperty("modifiers", out var mods))
        {
            var expected = ExpectedModifiers(mods);
            Assert.True(expected == evt.Modifiers,
                $"{context} — modifiers expected [{expected}] got [{evt.Modifiers}]");
        }
    }

    private static void AssertHeal(JsonElement expect, HealEvent evt, string context)
    {
        AssertString(expect, "healer", evt.Healer, context);
        AssertString(expect, "target", evt.Target, context);
        AssertString(expect, "spell", evt.Spell, context);
        AssertBool(expect, "overTime", evt.OverTime, context);

        if (expect.TryGetProperty("landed", out var landed))
        {
            Assert.True(landed.GetUInt32() == evt.Landed,
                $"{context} — landed expected {landed.GetUInt32()} got {evt.Landed}");
        }

        if (expect.TryGetProperty("potential", out var potential))
        {
            Assert.True(potential.GetUInt32() == evt.Potential,
                $"{context} — potential expected {potential.GetUInt32()} got {evt.Potential}");
        }

        if (expect.TryGetProperty("modifiers", out var mods))
        {
            var expected = ExpectedModifiers(mods);
            Assert.True(expected == evt.Modifiers,
                $"{context} — modifiers expected [{expected}] got [{evt.Modifiers}]");
        }
    }

    internal static HitModifiers ExpectedModifiers(JsonElement array)
    {
        var flags = HitModifiers.None;
        foreach (var item in array.EnumerateArray())
        {
            flags |= Enum.Parse<HitModifiers>(item.GetString()!);
        }

        return flags;
    }

    private static void AssertString(JsonElement expect, string name, string? actual, string context)
    {
        if (expect.TryGetProperty(name, out var prop))
        {
            var expected = prop.ValueKind == JsonValueKind.Null ? null : prop.GetString();
            Assert.True(expected == actual, $"{context} — {name} expected '{expected}' got '{actual}'");
        }
    }

    private static void AssertBool(JsonElement expect, string name, bool actual, string context)
    {
        if (expect.TryGetProperty(name, out var prop))
        {
            Assert.True(prop.GetBoolean() == actual,
                $"{context} — {name} expected {prop.GetBoolean()} got {actual}");
        }
    }

    private static void AssertEnum<T>(JsonElement expect, string name, T actual, string context)
        where T : struct, Enum
    {
        if (!expect.TryGetProperty(name, out var prop))
        {
            return;
        }

        var text = prop.GetString()!;
        var expected = text switch
        {
            "dd" => Enum.Parse<T>("DirectDamage"),
            "dot" => Enum.Parse<T>("DamageOverTime"),
            "ds" => Enum.Parse<T>("DamageShield"),
            _ => Enum.Parse<T>(text, ignoreCase: true),
        };
        Assert.True(expected.Equals(actual), $"{context} — {name} expected {expected} got {actual}");
    }
}
