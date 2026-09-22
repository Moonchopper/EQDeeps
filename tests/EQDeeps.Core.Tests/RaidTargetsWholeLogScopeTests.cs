using EQDeeps.Core.Events;
using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// C4 (ADR-022 Decision 4 / F31-3 brief Recon §4a): "every death in the log"
/// has no direct scope expression today. An empty scope on a Deaths query
/// aggregates per FIGHT span — <c>QueryEngine.ResolveScope</c>'s fallback
/// branch walks <c>_fights.Fights</c> — and <c>FightTracker.HandleDeath</c>
/// only extends a victim's fight if one is already active for them
/// (<c>_active.TryGetValue</c>); a death with nothing recorded against its
/// victim first never opens one, so it sits outside every unit an empty scope
/// can build and is silently dropped. The only scope that reads the raw
/// record stream with no fight involvement at all is <c>LastSeconds</c>
/// (ResolveScope's very first branch), anchored to the newest record's own
/// timestamp — which is what <c>RaidTargetsPanel.tsx</c>'s
/// <c>WHOLE_LOG_LAST_SECONDS</c> uses for exactly this reason.
/// </summary>
public class RaidTargetsWholeLogScopeTests
{
    private static readonly DateTime T0 = new(2024, 3, 9, 20, 0, 0);

    private readonly IdentityRegistry _identity = new();
    private readonly RecordStore _records = new();
    private readonly FightTracker _tracker;
    private readonly QueryEngine _engine;

    public RaidTargetsWholeLogScopeTests()
    {
        _tracker = new FightTracker(_identity);
        _engine = new QueryEngine(_records, _tracker, _identity, "Moonchopper");
    }

    private void Add(int t, GameEvent evt)
    {
        var timestamp = T0.AddSeconds(t);
        _records.Append(timestamp, evt);
        _tracker.Process(timestamp, evt);
    }

    [Fact]
    public void AnEmptyScopeMissesADeathWithNoFightButAGenerousLastSecondsCatchesIt()
    {
        // Lord Nagafen dies with no preceding damage record against him at
        // all: nothing ever opens a fight for him, so he never enters
        // _tracker.Fights and an empty scope's fight-based units cannot
        // include him.
        Add(0, new DeathEvent("Lord Nagafen", "Moonchopper"));

        var emptyScope = _engine.Execute(new QuerySpec
        {
            Source = QuerySource.Deaths,
            GroupBy = [Dimension.Player],
            Metrics = ["deaths"],
        });
        Assert.Empty(emptyScope.Rows);

        // 3600 s comfortably covers this handful-of-seconds test log with
        // margin to spare; the production view picks a bound sized to real
        // logs instead (years, not hours — see RaidTargetsPanel.tsx).
        var wholeLog = _engine.Execute(new QuerySpec
        {
            Source = QuerySource.Deaths,
            Scope = new QueryScope { LastSeconds = 3600 },
            GroupBy = [Dimension.Player],
            Metrics = ["deaths"],
        });
        var row = Assert.Single(wholeLog.Rows);
        Assert.Equal("Lord Nagafen", row.Key);
        Assert.Equal(1, row.Metrics["deaths"]);
    }

    /// <summary>
    /// The same case, but with a fight elsewhere on the timeline — proving the
    /// empty scope's drop is specific to a death whose timestamp falls outside
    /// every fight's window, not an artefact of an entirely fight-free log.
    /// Nagafen's death sits well clear of the rat's fight span so it cannot be
    /// caught by that fight's window incidentally covering the same instant.
    /// </summary>
    [Fact]
    public void AFoughtDeathStaysInScopeWhileTheUnfoughtOneIsStillDroppedByAnEmptyScope()
    {
        Add(0, new DamageEvent("Moonchopper", "A rat", 10, DamageKind.Melee, "Crushes"));
        Add(1, new DeathEvent("A rat", "Moonchopper")); // fought: inside the rat's own fight span [t0..t1]
        Add(100, new DeathEvent("Lord Nagafen", "Moonchopper")); // never fought: no active fight, no nearby one either

        var emptyScope = _engine.Execute(new QuerySpec
        {
            Source = QuerySource.Deaths,
            GroupBy = [Dimension.Player],
            Metrics = ["deaths"],
        });
        var row = Assert.Single(emptyScope.Rows);
        Assert.Equal("A rat", row.Key);

        var wholeLog = _engine.Execute(new QuerySpec
        {
            Source = QuerySource.Deaths,
            Scope = new QueryScope { LastSeconds = 3600 },
            GroupBy = [Dimension.Player],
            Metrics = ["deaths"],
        });
        Assert.Equal(2, wholeLog.Rows.Count);
        Assert.Contains(wholeLog.Rows, r => r.Key == "A rat");
        Assert.Contains(wholeLog.Rows, r => r.Key == "Lord Nagafen");
    }
}
