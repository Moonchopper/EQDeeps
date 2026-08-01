namespace EQDeeps.Core.Sessions;

public enum EntityKind
{
    Unknown,
    Player,
    Npc,
    Pet,
}

/// <summary>
/// Player/NPC/pet classification for one game server, shared by every session on
/// that server and grown over time from in-log evidence (domain doc §5): chat in
/// player channels, raid/group membership, /who output, pet-leader lines, and
/// death lines. There is no explicit tag on names, so classification is layered
/// heuristics with a correction flow — <see cref="PlayerVerified"/> fires when a
/// name previously assumed to be an NPC turns out to be a player, and fight
/// state deletes the phantom fights it created.
///
/// Thread-safe: sessions on the same server mutate it concurrently.
/// The whole state is exportable as a <see cref="IdentitySnapshot"/> for the
/// per-server persistence file (wired up in the app layer).
/// </summary>
public sealed class IdentityRegistry
{
    private readonly object _gate = new();
    private readonly HashSet<string> _verifiedPlayers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownNpcs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _petOwners = new(StringComparer.Ordinal);

    /// <summary>Raised when a name becomes a verified player (fight correction hooks here).</summary>
    public event Action<string>? PlayerVerified;

    /// <summary>Raised when a pet gains an owner mapping (query-time rollups pick it up).</summary>
    public event Action<string, string>? PetMapped;

    public void AddVerifiedPlayer(string name)
    {
        if (name.Length == 0 || name.Contains(' '))
        {
            return; // real player names are single words
        }

        bool added;
        lock (_gate)
        {
            added = _verifiedPlayers.Add(name);
            _knownNpcs.Remove(name);
        }

        if (added)
        {
            PlayerVerified?.Invoke(name);
        }
    }

    public void AddKnownNpc(string name)
    {
        if (name.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_verifiedPlayers.Contains(name))
            {
                return; // verification wins over NPC evidence
            }

            _knownNpcs.Add(name);
        }
    }

    public void MapPetToOwner(string pet, string owner)
    {
        if (pet.Length == 0 || owner.Length == 0)
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = !_petOwners.TryGetValue(pet, out var existing) || existing != owner;
            _petOwners[pet] = owner;
        }

        if (changed)
        {
            PetMapped?.Invoke(pet, owner);
        }
    }

    /// <summary>
    /// The pet's owner: explicit from possessive forms ("Kizante`s pet"),
    /// learned from pet-leader lines / (Owner: X) annotations otherwise.
    /// Null when the name is not a known pet.
    /// </summary>
    public string? OwnerOf(string name)
    {
        if (TryPossessiveOwner(name, out var possessive))
        {
            return possessive;
        }

        lock (_gate)
        {
            return _petOwners.TryGetValue(name, out var owner) ? owner : null;
        }
    }

    public bool IsVerifiedPlayer(string name)
    {
        lock (_gate)
        {
            return _verifiedPlayers.Contains(name);
        }
    }

    /// <summary>
    /// True when the name belongs to the players' side of a fight: a verified
    /// player, a mapped pet, or a possessive pet whose owner is not an NPC.
    /// </summary>
    public bool IsPlayerSide(string name)
    {
        if (TryPossessiveOwner(name, out var owner))
        {
            return !IsDefinitelyNpc(owner);
        }

        lock (_gate)
        {
            return _verifiedPlayers.Contains(name) || _petOwners.ContainsKey(name);
        }
    }

    /// <summary>
    /// True when the name can only be an NPC: it carries an article, has the
    /// multi-word shape NPC names use (player names are single words), or was
    /// seen dying to players. Verified players are never NPCs.
    /// </summary>
    public bool IsDefinitelyNpc(string name)
    {
        lock (_gate)
        {
            if (_verifiedPlayers.Contains(name))
            {
                return false;
            }

            if (_knownNpcs.Contains(name))
            {
                return true;
            }
        }

        if (TryPossessiveOwner(name, out _))
        {
            return false; // pets are handled by ownership, not NPC shape
        }

        return HasArticle(name) || name.Contains(' ');
    }

    public EntityKind Classify(string name)
    {
        if (OwnerOf(name) is not null)
        {
            return EntityKind.Pet;
        }

        if (IsVerifiedPlayer(name))
        {
            return EntityKind.Player;
        }

        return IsDefinitelyNpc(name) ? EntityKind.Npc : EntityKind.Unknown;
    }

    public IdentitySnapshot CreateSnapshot()
    {
        lock (_gate)
        {
            return new IdentitySnapshot(
                _verifiedPlayers.Order(StringComparer.Ordinal).ToArray(),
                _knownNpcs.Order(StringComparer.Ordinal).ToArray(),
                _petOwners.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => new PetMapping(p.Key, p.Value)).ToArray());
        }
    }

    public static IdentityRegistry FromSnapshot(IdentitySnapshot snapshot)
    {
        var registry = new IdentityRegistry();
        foreach (var player in snapshot.VerifiedPlayers)
        {
            registry._verifiedPlayers.Add(player);
        }

        foreach (var npc in snapshot.KnownNpcs)
        {
            registry._knownNpcs.Add(npc);
        }

        foreach (var pet in snapshot.PetOwners)
        {
            registry._petOwners[pet.Pet] = pet.Owner;
        }

        return registry;
    }

    private static bool HasArticle(string name) =>
        name.StartsWith("A ", StringComparison.Ordinal) ||
        name.StartsWith("An ", StringComparison.Ordinal) ||
        name.StartsWith("The ", StringComparison.Ordinal) ||
        name.StartsWith("a ", StringComparison.Ordinal) ||
        name.StartsWith("an ", StringComparison.Ordinal) ||
        name.StartsWith("the ", StringComparison.Ordinal);

    private static bool TryPossessiveOwner(string name, out string owner)
    {
        owner = string.Empty;
        foreach (var suffix in (ReadOnlySpan<string>)["`s pet", "'s pet"])
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
            {
                owner = name[..^suffix.Length];
                return true;
            }
        }

        return false;
    }
}

public sealed record PetMapping(string Pet, string Owner);

/// <summary>Serializable registry state for the per-server persistence file.</summary>
public sealed record IdentitySnapshot(
    IReadOnlyList<string> VerifiedPlayers,
    IReadOnlyList<string> KnownNpcs,
    IReadOnlyList<PetMapping> PetOwners);
