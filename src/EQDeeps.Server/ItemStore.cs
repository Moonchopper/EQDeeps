using System.Text.Json;
using EQDeeps.Core.Items;

namespace EQDeeps.Server;

/// <summary>
/// The items learned about each server, one small JSON per server (F29):
/// the <see cref="ItemRegistry"/> as it stood, written whenever a session
/// teaches it something new.
///
/// <para>Per server like the mob indexes (<see cref="MobHealthStore"/>) and
/// for the same reason — a name's meaning is a fact about a world, and every
/// character on it contributes. Two feeders: the log (loot, sales, purchases,
/// swept by the session's tick) and the player's own client files (the
/// loot-filter file and inventory dump, read from the install the log lives
/// in — read, never copied, as the maps are). The store also remembers which
/// client files it has read and how big they were, so a re-open costs a stat
/// and not a re-parse.</para>
///
/// <para>Recomputable: every fact came from a log or a file that still
/// exists, so a corrupt store starts fresh. Atomic writes (temp + move) like
/// every other store, and a disk that will not take a write costs the user
/// nothing but a re-learn next launch.</para>
/// </summary>
public sealed class ItemStore
{
    private readonly string _root;
    private readonly object _gate = new();
    private readonly Dictionary<string, ItemRegistry> _registries = new(StringComparer.Ordinal);

    /// <summary>Client files already folded in, by path, with the length and write time they had.</summary>
    private readonly Dictionary<string, (long Length, DateTime Written)> _files = new(StringComparer.OrdinalIgnoreCase);

    public ItemStore(string? root = null)
    {
        _root = Path.Combine(
            root ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQDeeps"),
            "items");
    }

    /// <summary>The server's registry, loaded from disk on first ask. Callers read it directly; it is thread-safe.</summary>
    public ItemRegistry For(string server)
    {
        lock (_gate)
        {
            return LoadLocked(server);
        }
    }

    /// <summary>
    /// Folds log sightings into the server's registry and persists if the
    /// name list grew. Counts tick on every replay of a log — the store does
    /// not know one loot line from another — so callers hand it only what
    /// is past their own watermark, and a re-open replays nothing.
    /// </summary>
    public void Observe(string server, IEnumerable<(string Name, DateTime At, ItemSource Source, int Quantity)> sightings)
    {
        lock (_gate)
        {
            var registry = LoadLocked(server);
            var before = registry.Version;
            foreach (var (name, at, source, quantity) in sightings)
            {
                registry.Observe(name, at, source, quantity);
            }

            if (registry.Version != before)
            {
                WriteLocked(server, registry);
            }
        }
    }

    /// <summary>
    /// Reads the client's item files for a character on a server, if the
    /// install has them and they have changed since last read. Returns how
    /// many rows taught the registry something. Missing files are the normal
    /// case for a log copied out of its game folder, and are not an error.
    /// </summary>
    public int ReadClientFiles(string server, string? installRoot, string character)
    {
        if (string.IsNullOrEmpty(installRoot))
        {
            return 0;
        }

        var learned = 0;
        lock (_gate)
        {
            var registry = LoadLocked(server);
            var before = registry.Version;

            var filter = LootFilterFile.PathFor(installRoot, character, server);
            if (Changed(filter, out var text) && text is not null)
            {
                foreach (var row in LootFilterFile.Parse(text))
                {
                    if (registry.Learn(row.Name, row.ItemId, row.IconId, ItemSource.LootFilter))
                    {
                        learned++;
                    }
                }
            }

            var inventory = InventoryDump.PathFor(installRoot, character, server);
            if (Changed(inventory, out text) && text is not null)
            {
                foreach (var row in InventoryDump.Parse(text))
                {
                    if (registry.Learn(row.Name, row.ItemId, null, ItemSource.Inventory))
                    {
                        learned++;
                    }
                }
            }

            if (registry.Version != before)
            {
                WriteLocked(server, registry);
            }
        }

        return learned;
    }

    /// <summary>
    /// True (with the text) when the file exists and is not the one already
    /// read. The game rewrites the loot-filter file whole, so length + write
    /// time is enough to tell; a file that fails to open is skipped this
    /// time and tried again next time.
    /// </summary>
    private bool Changed(string path, out string? text)
    {
        text = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            var stamp = (info.Length, info.LastWriteTimeUtc);
            if (_files.TryGetValue(path, out var seen) && seen == stamp)
            {
                return false;
            }

            // The game may hold the file open for writing; share everything.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
            _files[path] = stamp;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private ItemRegistry LoadLocked(string server)
    {
        var key = Sanitize(server);
        if (_registries.TryGetValue(key, out var registry))
        {
            return registry;
        }

        registry = ItemRegistry.FromSnapshot(ReadLocked(PathFor(key)));
        _registries[key] = registry;
        return registry;
    }

    private string PathFor(string sanitizedServer) => Path.Combine(_root, $"{sanitizedServer}.json");

    private static string Sanitize(string value)
    {
        var cleaned = new string(value
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .ToArray());
        return cleaned.Length > 0 ? cleaned : "unknown";
    }

    private void WriteLocked(string server, ItemRegistry registry)
    {
        var path = PathFor(Sanitize(server));
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(temp, JsonSerializer.Serialize(registry.Snapshot()));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static List<ItemRecord> ReadLocked(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<ItemRecord>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
