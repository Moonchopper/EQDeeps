using EQDeeps.Core.Events;
using EQDeeps.Core.Ingestion;
using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;
using EQDeeps.TestSupport;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// The release gate (CLAUDE.md §8): things that must be true of any parse,
/// checked against the real pipeline over a generated log.
///
/// <para><b>Why these and not a second parser.</b> Comparing our numbers with
/// EQLogParser's was the plan for a long time and was retired: it parses live
/// EverQuest while this app is used on Legends, and the two disagree by design
/// about denominators. What is left is stronger in one respect — an invariant
/// cannot be satisfied by a wrong answer that happens to look plausible. A
/// double-counted pet, a damage shield credited to both sides, a fight that
/// swallowed a record belonging to its neighbour: each of those produces a
/// believable number and breaks one of the identities below.</para>
///
/// <para>These run on a synthetic log because CI has no real one. The
/// generator emits what real logs contain — pets, mercs, damage shields,
/// deaths, chat noise, the two-entries-on-one-line glitch — so the identities
/// are exercised against the awkward cases rather than a tidy stream.</para>
/// </summary>
public sealed class ReleaseGateInvariantTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A generated log and the session that parsed it, once per test.</summary>
    private async Task<(Session Session, string[] Lines)> ParseAsync(TimeSpan duration)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "eqlog_Kizant_xegony.txt");
        var lines = new SyntheticLogGenerator(seed: 4242).Lines(duration).ToArray();
        await File.WriteAllLinesAsync(path, lines);

        var session = new Session(path, ingestOptions: new IngestOptions { Follow = false });
        await session.RunAsync(CancellationToken.None);
        return (session, lines);
    }

    [Fact]
    public async Task EveryLineIsEitherParsedOrCounted()
    {
        var (session, lines) = await ParseAsync(TimeSpan.FromMinutes(20));
        using var _ = session;

        // Nothing may vanish: a line the parser does not understand has to be
        // counted, not dropped quietly, or "unrecognized is zero" would be a
        // claim about silence rather than about coverage.
        Assert.True(session.Records.Count > 0, "the generator produced no records at all");
        Assert.Equal(0, session.ParserFailures);

        // The generator only emits shapes this parser claims to know, so an
        // unrecognized line here is a real gap in the grammars — the corpus's
        // own fidelity check, run over a whole log rather than line by line.
        Assert.Equal(0, session.UnrecognizedLines);

        // And every record came from a line: no parse may invent more events
        // than there were lines to read.
        Assert.True(
            session.Records.Count <= lines.Length,
            $"{session.Records.Count} records from {lines.Length} lines");
    }

    [Fact]
    public async Task AFightsTotalIsExactlyWhatItsActorsContributed()
    {
        var (session, _) = await ParseAsync(TimeSpan.FromMinutes(20));
        using var _s = session;

        var fights = session.Fights.Fights.ToArray();
        Assert.NotEmpty(fights);

        foreach (var fight in fights)
        {
            // The headline number and the breakdown behind it are accumulated
            // in the same place; if they ever diverge, one of them is being
            // written without the other.
            Assert.Equal(fight.DamageTotal, fight.DamageByActor.Values.Sum(a => a.Total));
            Assert.Equal(fight.TankingTotal, fight.TankingByDefender.Values.Sum(a => a.Total));

            // A fight that recorded damage has to have someone who dealt it.
            if (fight.DamageTotal > 0)
            {
                Assert.NotEmpty(fight.DamageByActor);
            }
        }
    }

    [Fact]
    public async Task EveryDamageRecordLandsInOneFightOrIsDeliberatelyDropped()
    {
        var (session, _) = await ParseAsync(TimeSpan.FromMinutes(20));
        using var _s = session;

        // What the fights between them claim.
        var claimed = session.Fights.Fights.Sum(f => f.DamageTotal + f.TankingTotal);

        // What the record stream holds, minus the cases the tracker documents
        // as having no fight to belong to: player against player, NPC against
        // NPC, and anything where neither side could be placed. Recomputing
        // that from the records — rather than trusting the tracker — is the
        // point: if attribution silently swallowed or duplicated a record,
        // these two totals stop agreeing.
        long attributable = 0;
        foreach (var (_, evt) in session.Records.Range(DateTime.MinValue, DateTime.MaxValue))
        {
            if (evt is not DamageEvent damage)
            {
                continue;
            }

            var attackerIsNpc = damage.Attacker is { } a && session.Identity.IsDefinitelyNpc(a);
            var defenderIsNpc = session.Identity.IsDefinitelyNpc(damage.Defender);
            if (attackerIsNpc == defenderIsNpc)
            {
                continue; // both sides the same kind: no fight, by design
            }

            attributable += damage.Amount;
        }

        // Attribution may legitimately account for less than the stream holds
        // — a spell with no recent cast is environmental and belongs to
        // nobody — but it must never account for more, which is what
        // double-counting looks like.
        Assert.True(
            claimed <= attributable,
            $"fights claim {claimed:N0} damage from a stream holding {attributable:N0} attributable");

        // And it must not lose most of it either; a collapse here means
        // records are falling out of attribution entirely.
        Assert.True(
            claimed >= attributable / 2,
            $"fights claim only {claimed:N0} of {attributable:N0} attributable damage");
    }

    [Fact]
    public async Task TheQueryEngineAgreesWithTheFightTracker()
    {
        var (session, _) = await ParseAsync(TimeSpan.FromMinutes(20));
        using var _s = session;

        // The cross-subsystem check, and the one worth having: the fight
        // tracker accumulates as records arrive, the query engine re-derives
        // from the record stream over a scope. They are independent paths to
        // the same number, so agreement is evidence that neither the scope
        // resolution nor the accumulation is quietly wrong.
        var engine = new QueryEngine(session);
        var fights = session.Fights.Fights.Where(f => f is { Closed: true, DamageTotal: > 0 }).Take(5).ToArray();
        Assert.NotEmpty(fights);

        foreach (var fight in fights)
        {
            var result = engine.Execute(new QuerySpec
            {
                Source = QuerySource.Damage,
                Scope = new QueryScope { FightIds = [fight.Id] },
                GroupBy = [Dimension.Player],
                Metrics = ["total"],
            });

            var queried = result.Rows.Sum(r => r.Metrics.TryGetValue("total", out var t) ? t : 0);
            Assert.Equal(fight.DamageTotal, (long)queried);

            // The totals block and the rows under it must agree too.
            Assert.Equal(queried, result.Totals.TryGetValue("total", out var all) ? all : -1);
        }
    }
}
