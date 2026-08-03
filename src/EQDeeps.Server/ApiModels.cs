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
    long MalformedLines,
    /// <summary>Stance switches by this character — gates the Stances view.</summary>
    long StanceSwitches = 0);

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
    int GroupIndex)
{
    public static List<FightInfo> Build(IReadOnlyList<Fight> fights)
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
                f.DamageTotal, f.TankingTotal, f.TauntCount, groupIndex[f.Id]))
            .ToList();
    }
}
