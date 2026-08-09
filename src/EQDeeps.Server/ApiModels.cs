using EQDeeps.Core.Mobs;
using EQDeeps.Core.Parsing;
using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;
using EQDeeps.Server.Updates;

namespace EQDeeps.Server;

public sealed record OpenSessionRequest(string Path, DateTime? BackfillFrom = null, bool EmuMode = false);

/// <summary>How long a "no thanks" to an update should last.</summary>
public enum DeferScope
{
    /// <summary>Until the app restarts, or the user checks by hand.</summary>
    Once,

    /// <summary>Until a release newer than the one offered ships.</summary>
    Release,

    /// <summary>Until the user is running a different version than they are now.</summary>
    CurrentVersion,
}

public sealed record DeferUpdateRequest(DeferScope Scope);

/// <summary>
/// Consent to install. <paramref name="ApplyWhenReady"/> distinguishes "update
/// now" (restart as soon as the download lands) from the default, which waits
/// until the user closes the app on their own terms.
/// </summary>
public sealed record StageUpdateRequest(bool ApplyWhenReady = false);

public sealed record SetUpdateModeRequest(UpdateMode Mode);

/// <summary>Timeline scope; a record wrapper so filters (kinds, actors) can grow in later.</summary>
public sealed record TimelineRequest(QueryScope Scope);

/// <summary>
/// Which incoming swings to hand back and how many (F26).
/// </summary>
/// <param name="OwnerOnly">
/// Restrict to this log's own character. Resolved server-side against whichever
/// log is open rather than by naming them, so the setting means "me" on every
/// session instead of meaning one character everywhere — the same rule the
/// stance panels follow.
/// </param>
public sealed record IncomingHitsRequest(
    QueryScope Scope,
    int? Limit = null,
    bool OwnerOnly = false,
    IReadOnlyList<string>? Defenders = null);

public sealed record SessionInfo(
    string Id,
    string Path,
    string Character,
    string Server,
    bool BackfillComplete,
    int RecordCount,
    int FightCount,
    long UnrecognizedLines,
    long MalformedLines,
    /// <summary>Stance switches by this character — gates the Stances view.</summary>
    long StanceSwitches = 0);

/// <summary>
/// Everything learned about one server's mobs (F25). The estimates are the
/// whole of it; the counts are there so the panel can say how much evidence is
/// behind what it is showing rather than presenting a first-night guess with
/// the same face as a thousand-kill average.
/// </summary>
/// <param name="Instanced">
/// Whether any of it came from an instance. On a server with no difficulty
/// tiers the tier columns are noise, so the client asks this rather than
/// inferring it from rows that happen to be on screen.
/// </param>
public sealed record MobHealthReport(
    string Server,
    List<MobHealthEstimate> Mobs,
    int Kills,
    bool Instanced);

/// <summary>
/// What this server's mobs do to the people in front of them (F26).
/// </summary>
/// <param name="Character">
/// Whose log is asking. The profiles are the server's, but which of them are
/// about <i>this</i> character is a question only the session can answer.
/// </param>
/// <param name="CharacterLevel">
/// The level the log last established for that character, or null if it never
/// did. It picks which rows the panel opens on, and its absence is reported
/// rather than guessed around — a level-58 shown a level-40's numbers would be
/// reading someone else's fight.
/// </param>
/// <param name="Landed">Hits behind the whole report, so a first-night guess does not wear the face of an evening's evidence.</param>
public sealed record MobAttackReport(
    string Server,
    string Character,
    int? CharacterLevel,
    List<MobAttackEstimate> Mobs,
    int Landed,
    bool Instanced);

public sealed record FightInfo(
    int Id,
    string Name,
    DateTime BeginTime,
    DateTime LastDamageTime,
    bool Dead,
    bool Closed,
    long DamageTotal,
    long TankingTotal,
    int TauntCount,
    int GroupIndex,
    /// <summary>
    /// The instance difficulty this was fought at, null in the open world —
    /// which is also what a tier-0 instance reads as, since the log writes the
    /// two identically. See <see cref="InstanceZone"/>.
    /// </summary>
    int? Difficulty,
    /// <summary>
    /// Learned health for this mob at this zone and difficulty (F25), null
    /// until enough of them have been killed. Paired with
    /// <see cref="DamageTotal"/> it says whether this fight was a whole kill or
    /// a share of one.
    /// </summary>
    long? EstimatedHealth,
    /// <summary>
    /// This session's own character and their pets, out of
    /// <see cref="DamageTotal"/>. One number rather than the whole per-actor
    /// map: it keeps the fight list cheap at raid scale while giving the
    /// client a per-fight series for its own character — which is what any
    /// comparison across unequal windows has to be built from, since totals
    /// over a 36-minute set and a 2-minute one are not comparable at all.
    /// </summary>
    long CharacterDamage)
{
    /// <param name="health">
    /// Learned mob health keyed by <see cref="MobHealthStore.KeyOf"/>, or null
    /// when the store is not attached (tests, and any build that has never
    /// recorded a kill). A missing entry is normal, not an error: a mob nobody
    /// has killed enough of simply has no number yet.
    /// </param>
    public static List<FightInfo> Build(
        IReadOnlyList<Fight> fights,
        string character,
        IdentityRegistry identity,
        IReadOnlyDictionary<string, MobHealthEstimate>? health = null)
    {
        var groupIndex = new Dictionary<int, int>();
        var groups = FightTracker.Group(fights);
        for (var g = 0; g < groups.Count; g++)
        {
            foreach (var fight in groups[g])
            {
                groupIndex[fight.Id] = g;
            }
        }

        return fights
            .Select(f => new FightInfo(
                f.Id, f.Name, f.BeginTime, f.LastDamageTime, f.Dead, f.Closed,
                f.DamageTotal, f.TankingTotal, f.TauntCount, groupIndex[f.Id],
                f.Zone?.Difficulty,
                HealthOf(f, health),
                OwnDamage(f, character, identity)))
            .ToList();
    }

    private static long? HealthOf(
        Fight fight, IReadOnlyDictionary<string, MobHealthEstimate>? health)
    {
        if (health is null || fight.Zone is not { BaseName.Length: > 0 } zone)
        {
            return null;
        }

        return health.TryGetValue(
            MobHealthStore.KeyOf(fight.Name, zone.BaseName, zone.Difficulty), out var estimate)
            ? estimate.Health
            : null;
    }

    /// <summary>
    /// Pets roll up to their owner here unconditionally. A pet's damage is the
    /// player's doing whatever the display toggle says, and a per-fight series
    /// that flickered as that toggle moved would compare two different things.
    /// </summary>
    private static long OwnDamage(Fight fight, string character, IdentityRegistry identity)
    {
        var total = 0L;
        foreach (var (actor, totals) in fight.DamageByActor)
        {
            if (actor.Equals(character, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(identity.OwnerOf(actor), character, StringComparison.OrdinalIgnoreCase))
            {
                total += totals.Total;
            }
        }

        return total;
    }
}
