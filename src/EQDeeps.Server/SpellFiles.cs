using EQDeeps.Core.Spells;

namespace EQDeeps.Server;

/// <summary>
/// Reads the spell files out of the game install a log sits in, and keeps one
/// book per install for as long as the app runs.
///
/// <para>Read, never bundled — the same rule as the maps (F27) and the
/// loot-filter file (F29): the files belong to the player, they are already on
/// their disk, and a copy in this repo would be both a licence question and a
/// stale one. A log opened from outside a game folder simply gets an empty
/// book, and the emote grammars then match nothing.</para>
///
/// <para>Cached by install path because it is per client, not per server or
/// per character, and because 44 MB of text is not worth re-reading for the
/// second character on the same machine. Reading and indexing both files
/// measures well under a second, so there is no disk cache of the result: the
/// source files are right there.</para>
/// </summary>
public sealed class SpellLibrary
{
    private readonly Dictionary<string, SpellBook> _byInstall = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly bool _enabled;

    public SpellLibrary(bool enabled = true) => _enabled = enabled;

    /// <summary>
    /// The book for the install a log lives in, read once per install. Empty
    /// when there is no install, no files, or nothing readable in them — every
    /// one of which is an ordinary situation rather than an error.
    /// </summary>
    public SpellBook For(string? installRoot)
    {
        if (!_enabled || string.IsNullOrEmpty(installRoot))
        {
            return SpellBook.Empty;
        }

        lock (_gate)
        {
            if (_byInstall.TryGetValue(installRoot, out var cached))
            {
                return cached;
            }

            var book = Read(installRoot);
            _byInstall[installRoot] = book;
            return book;
        }
    }

    private static SpellBook Read(string installRoot)
    {
        try
        {
            var spells = Path.Combine(installRoot, "spells_us.txt");
            var strings = Path.Combine(installRoot, "spells_us_str.txt");
            if (!File.Exists(spells) || !File.Exists(strings))
            {
                return SpellBook.Empty;
            }

            // Latin-1 for the same reason ingestion uses it: these are the
            // client's own files and carry its byte-per-character text.
            return SpellBook.Build(
                File.ReadAllText(spells, System.Text.Encoding.Latin1),
                File.ReadAllText(strings, System.Text.Encoding.Latin1));
        }
        catch (IOException)
        {
            return SpellBook.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return SpellBook.Empty;
        }
    }
}
