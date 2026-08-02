using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;

namespace EQDeeps.Server;

public sealed record OpenSessionRequest(string Path, DateTime? BackfillFrom = null, bool EmuMode = false);

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
