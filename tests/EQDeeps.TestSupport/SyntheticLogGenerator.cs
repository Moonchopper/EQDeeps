using System.Globalization;
using System.Text;

namespace EQDeeps.TestSupport;

/// <summary>
/// Deterministic generator of realistic EverQuest raid-log content: pull cycles
/// with melee/spell/heal/chat mixes, avoidance, pet lines, deaths, zone lines,
/// occasional glitched double-entry physical lines, and 1-second timestamp
/// pacing with multi-line bursts. Powers unit fixtures, benchmarks, and
/// end-to-end tests — there is no EverQuest in the loop anywhere.
/// </summary>
public sealed class SyntheticLogGenerator
{
    private readonly Random _random;
    private readonly string[] _players;
    private readonly string[] _pets;
    private DateTime _time;

    private static readonly string[] Npcs =
    [
        "an ice giant", "a shadow drake", "Doomshade", "Grendish the Crusader",
        "a primal guardian", "Ogna, Artisan of War",
    ];

    private static readonly string[] Verbs = ["crushes", "slashes", "pierces", "hits", "kicks", "bashes", "claws"];

    private static readonly string[] Spells =
    [
        "Burst of Flames", "Chromospheric Vortex Rk. II", "Pyre of Klraggek Rk. III",
        "Mind Coil Rk. II", "Curse of the Shrine", "Elemental Conversion VI",
        "Spirit of the Wood XXXIV", "Blessing of the Ancients III", "Ardent Elixir Rk. II",
        "Aria of Absolution",
    ];

    private static readonly string[] Modifiers =
    [
        "(Critical)", "(Lucky Critical)", "(Twincast)", "(Lucky Critical Twincast)",
        "(Strikethrough)", "(Flurry)", "(Riposte)", "(Critical Flurry)",
    ];

    private static readonly string[] ChatTemplates =
    [
        "{0} tells the raid, 'inc 3 giants'",
        "{0} tells the guild, 'grats!'",
        "{0} says, 'You have been slain by an armed flyer!'",
        "{0} tells the group, 'need a heal over here'",
        "{0} auctions, 'WTS Cold-Forged Cudgel'",
        "{0} shouts, 'train to zone!'",
    ];

    public SyntheticLogGenerator(int seed = 1337, int playerCount = 54, DateTime? start = null)
    {
        _random = new Random(seed);
        _time = start ?? new DateTime(2024, 3, 9, 20, 0, 0);
        _players = new string[playerCount];
        _pets = new string[Math.Max(1, playerCount / 6)];
        for (var i = 0; i < playerCount; i++)
        {
            _players[i] = $"Raider{i + 1:D2}";
        }

        for (var i = 0; i < _pets.Length; i++)
        {
            _pets[i] = $"Xob{(char)('a' + i)}tik";
        }
    }

    public DateTime CurrentTime => _time;

    /// <summary>
    /// Generates physical lines (a rare line contains two glitched entries) for
    /// the given duration of simulated raid time, advancing the internal clock.
    /// </summary>
    public IEnumerable<string> Lines(TimeSpan duration)
    {
        var end = _time + duration;
        while (_time < end)
        {
            // One pull, then a breather.
            var npc = Npcs[_random.Next(Npcs.Length)];
            var fightSeconds = _random.Next(25, 90);
            for (var s = 0; s < fightSeconds && _time < end; s++, _time = _time.AddSeconds(1))
            {
                var burst = _random.Next(5, 30);
                string? held = null;
                for (var i = 0; i < burst; i++)
                {
                    var line = Prefix(_time) + Message(npc);
                    if (held is not null)
                    {
                        yield return held + line; // the two-entries-on-one-line glitch
                        held = null;
                    }
                    else if (_random.Next(4000) == 0)
                    {
                        held = line;
                    }
                    else
                    {
                        yield return line;
                    }
                }

                if (held is not null)
                {
                    yield return held;
                }
            }

            if (_time < end)
            {
                yield return Prefix(_time) + npc.ToUpperInvariant()[0] + npc[1..] + " died.";
            }

            var breather = _random.Next(4, 20);
            for (var s = 0; s < breather && _time < end; s++, _time = _time.AddSeconds(1))
            {
                if (_random.Next(3) == 0)
                {
                    yield return Prefix(_time) + IdleMessage();
                }
            }
        }
    }

    /// <summary>Writes at least <paramref name="targetBytes"/> of log to a file; returns bytes written.</summary>
    public long WriteFile(string path, long targetBytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        using var writer = new StreamWriter(stream, Encoding.Latin1);
        long written = 0;
        while (written < targetBytes)
        {
            foreach (var line in Lines(TimeSpan.FromMinutes(10)))
            {
                writer.WriteLine(line);
                written += line.Length + 2;
                if (written >= targetBytes)
                {
                    break;
                }
            }
        }

        writer.Flush();
        return written;
    }

    public static string Prefix(DateTime time) =>
        $"[{time.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture)}] ";

    private string Message(string npc)
    {
        var player = _players[_random.Next(_players.Length)];
        var roll = _random.Next(100);
        return roll switch
        {
            < 35 => $"{player} {Verbs[_random.Next(Verbs.Length)]} {npc} for {Amount()} points of damage.{ModifierSuffix()}",
            < 45 => $"{Cap(npc)} {Verbs[_random.Next(Verbs.Length)]} {player} for {Amount()} points of damage.{ModifierSuffix()}",
            < 52 => $"{player} tries to crush {npc}, but misses!",
            < 55 => $"{Cap(npc)} tries to hit {player}, but {player} dodges!",
            < 63 => $"{player} hit {npc} for {Amount()} points of fire damage by {Spell()}.{ModifierSuffix()}",
            < 71 => $"{Cap(npc)} has taken {Amount()} damage from {Spell()} by {player}.",
            < 78 => $"{player} healed {_players[_random.Next(_players.Length)]} for {Amount()} hit points by {Spell()}.",
            < 81 => $"{player} healed {_players[_random.Next(_players.Length)]} over time for {_random.Next(1000, 9000)} ({_random.Next(9000, 20000)}) hit points by {Spell()}.",
            < 85 => $"{_pets[_random.Next(_pets.Length)]} {Verbs[_random.Next(Verbs.Length)]} {npc} for {Amount()} points of damage.",
            < 89 => $"{Cap(npc)} is pierced by {player}'s thorns for {_random.Next(100, 9000)} points of non-melee damage.",
            < 93 => $"{player} begins casting {Spell()}.",
            < 97 => string.Format(CultureInfo.InvariantCulture, ChatTemplates[_random.Next(ChatTemplates.Length)], player),
            _ => $"You have taken {_random.Next(500, 40000)} damage from {Spell()} by {Cap(npc)}.",
        };
    }

    private string IdleMessage()
    {
        var player = _players[_random.Next(_players.Length)];
        return _random.Next(4) switch
        {
            0 => string.Format(CultureInfo.InvariantCulture, ChatTemplates[_random.Next(ChatTemplates.Length)], player),
            1 => $"{player} begins casting {Spell()}.",
            2 => $"{player} healed {player} for {_random.Next(500, 20000)} hit points by {Spell()}.",
            _ => $"{_pets[_random.Next(_pets.Length)]} says 'My leader is {player}'",
        };
    }

    private int Amount() => _random.Next(800, 2_000_000);

    private string Spell() => Spells[_random.Next(Spells.Length)];

    private string ModifierSuffix() =>
        _random.Next(100) < 30 ? " " + Modifiers[_random.Next(Modifiers.Length)] : string.Empty;

    private static string Cap(string name) =>
        char.IsLower(name[0]) ? char.ToUpperInvariant(name[0]) + name[1..] : name;
}
