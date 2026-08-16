namespace EQDeeps.Core.Items;

/// <summary>Where the registry learned about an item; a record accumulates these.</summary>
[Flags]
public enum ItemSource
{
    None = 0,
    /// <summary>The client's loot-filter file: id, icon and name.</summary>
    LootFilter = 1,
    /// <summary>An <c>/outputfile inventory</c> dump: id and name.</summary>
    Inventory = 2,
    /// <summary>A loot line in the log.</summary>
    Looted = 4,
    /// <summary>Sold to a merchant.</summary>
    Sold = 8,
    /// <summary>Bought from a merchant.</summary>
    Bought = 16,
}

/// <summary>
/// One item as the app knows it: the base name, the game's id and icon when
/// a client file has supplied them, and what the log has said about it.
/// </summary>
public sealed record ItemRecord(
    string Name,
    int? Id,
    int? IconId,
    DateTime? FirstSeen,
    DateTime? LastSeen,
    ItemSource Sources,
    int Looted,
    int Sold,
    int Bought);

/// <summary>
/// The items a server's logs and client files have named (F29). The log
/// gives names — a loot line, a sale, a purchase — and never ids; the
/// player's own files (<see cref="LootFilterFile"/>, <see cref="InventoryDump"/>)
/// give ids for the items they list. This is where the two meet, keyed by
/// <see cref="ItemNames.Key"/> so that "Fine Steel Rapier +2" looted, "Fine
/// Steel Rapier" in the filter file and "fine steel rapier" typed in chat are
/// one row.
///
/// <para>Per server, like the mob indexes: item ids are the same across
/// Legends servers as far as anyone can tell, but a name's meaning is not
/// promised to be, and the cost of a per-server file is nothing.</para>
///
/// <para>Thread-safe: the session's tick feeds it while requests read it.
/// A cache — every fact in it can be re-derived from files that still exist,
/// so a corrupt store is discarded, never repaired.</para>
/// </summary>
public sealed class ItemRegistry
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Bumped on every change, so a caller can tell "nothing new" from a full read.</summary>
    public int Version { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// A fact from a client file: this name has this id (and icon). Returns
    /// whether anything changed. A later file wins on id and icon — the
    /// files are the authority — but the name kept for display is the first
    /// one seen from a file, since files use the client's own casing.
    /// </summary>
    public bool Learn(string name, int id, int? iconId, ItemSource source)
    {
        var key = ItemNames.Key(name);
        if (key.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(ItemNames.Strip(name));
                _entries[key] = entry;
            }
            else if (!entry.NamedByFile)
            {
                // A file's casing outranks the log's ("Raw-Hide" as the client
                // spells it, not however the loot line had it).
                entry.Name = ItemNames.Strip(name);
            }

            var changed = entry.Id != id || (iconId.HasValue && entry.IconId != iconId) ||
                          (entry.Sources & source) != source || !entry.NamedByFile;
            entry.Id = id;
            entry.IconId = iconId ?? entry.IconId;
            entry.Sources |= source;
            entry.NamedByFile = true;
            if (changed)
            {
                Version++;
            }

            return changed;
        }
    }

    /// <summary>
    /// A sighting in the log: looted, sold or bought, at a time. Returns
    /// whether the item was new to the registry — a caller banking a batch
    /// wants to know if the name list grew, not whether a count ticked.
    /// </summary>
    public bool Observe(string name, DateTime at, ItemSource source, int quantity = 1)
    {
        var key = ItemNames.Key(name);
        if (key.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            var isNew = !_entries.TryGetValue(key, out var entry);
            if (isNew)
            {
                entry = new Entry(ItemNames.Strip(name));
                _entries[key] = entry!;
            }

            entry!.Sources |= source;
            if (entry.FirstSeen is null || at < entry.FirstSeen)
            {
                entry.FirstSeen = at;
            }

            if (entry.LastSeen is null || at > entry.LastSeen)
            {
                entry.LastSeen = at;
            }

            switch (source)
            {
                case ItemSource.Looted:
                    entry.Looted += quantity;
                    break;
                case ItemSource.Sold:
                    entry.Sold += quantity;
                    break;
                case ItemSource.Bought:
                    entry.Bought += quantity;
                    break;
            }

            Version++;
            return isNew;
        }
    }

    /// <summary>What is known about a name, under any decoration or casing; null when nothing is.</summary>
    public ItemRecord? Find(string name)
    {
        var key = ItemNames.Key(name);
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var entry) ? entry.ToRecord() : null;
        }
    }

    /// <summary>Every item, in no particular order.</summary>
    public IReadOnlyList<ItemRecord> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<ItemRecord>(_entries.Count);
            foreach (var entry in _entries.Values)
            {
                list.Add(entry.ToRecord());
            }

            return list;
        }
    }

    /// <summary>The display names, for building a mention scanner.</summary>
    public IReadOnlyList<string> Names()
    {
        lock (_gate)
        {
            var list = new List<string>(_entries.Count);
            foreach (var entry in _entries.Values)
            {
                list.Add(entry.Name);
            }

            return list;
        }
    }

    /// <summary>Rebuilds a registry from a stored snapshot; unknown or blank rows are skipped, not thrown on.</summary>
    public static ItemRegistry FromSnapshot(IEnumerable<ItemRecord> records)
    {
        var registry = new ItemRegistry();
        foreach (var r in records)
        {
            if (string.IsNullOrWhiteSpace(r.Name))
            {
                continue;
            }

            var key = ItemNames.Key(r.Name);
            registry._entries[key] = new Entry(ItemNames.Strip(r.Name))
            {
                Id = r.Id,
                IconId = r.IconId,
                FirstSeen = r.FirstSeen,
                LastSeen = r.LastSeen,
                Sources = r.Sources,
                Looted = r.Looted,
                Sold = r.Sold,
                Bought = r.Bought,
                NamedByFile = (r.Sources & (ItemSource.LootFilter | ItemSource.Inventory)) != 0,
            };
        }

        return registry;
    }

    private sealed class Entry(string name)
    {
        public string Name = name;
        public int? Id;
        public int? IconId;
        public DateTime? FirstSeen;
        public DateTime? LastSeen;
        public ItemSource Sources;
        public int Looted;
        public int Sold;
        public int Bought;
        public bool NamedByFile;

        public ItemRecord ToRecord() => new(Name, Id, IconId, FirstSeen, LastSeen, Sources, Looted, Sold, Bought);
    }
}
