using EQDeeps.Core.Gear;
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

public sealed record SessionInfo(
    string Id,
    string Path,
    string Character,
    string Server,
    bool BackfillComplete,
    int RecordCount,
    int FightCount,
    long UnrecognizedLines,
    long MalformedLines);

/// <summary>
/// Whether we can say anything about this character's gear, and what to tell
/// them if not.
///
/// <para><paramref name="ExpectedPath"/> and <paramref name="Command"/> travel
/// with the status deliberately: when a player types the command and nothing
/// appears, the useful answer is the exact path being watched, not "no gear
/// found".</para>
///
/// <para><paramref name="FightsSince"/> is the honest measure of staleness —
/// gear can change at any time without a trace in the log, so the only true
/// statement is how much combat has happened since the last proof.</para>
/// </summary>
public sealed record GearStatus(
    bool HasSnapshot,
    DateTime? CapturedAt,
    int FightsSince,
    string ExpectedPath,
    string Command);

public sealed record GearReport(
    List<GearSnapshot> Snapshots,
    List<GearChange> Changes,
    GearStatus Status);

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
    /// This session's own character and their pets, out of
    /// <see cref="DamageTotal"/>. One number rather than the whole per-actor
    /// map: it keeps the fight list cheap at raid scale while giving the
    /// client a per-fight series for its own character — which is what any
    /// comparison across unequal windows has to be built from, since totals
    /// over a 36-minute set and a 2-minute one are not comparable at all.
    /// </summary>
    long CharacterDamage)
{
    public static List<FightInfo> Build(
        IReadOnlyList<Fight> fights, string character, IdentityRegistry identity)
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
                OwnDamage(f, character, identity)))
            .ToList();
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
