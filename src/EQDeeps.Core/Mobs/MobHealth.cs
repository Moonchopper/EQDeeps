using EQDeeps.Core.Sessions;

namespace EQDeeps.Core.Mobs;

/// <summary>
/// What one kill cost: the damage the mob absorbed between the first hit that
/// opened its fight and the line that said it was dead.
///
/// <para>Kept as raw samples rather than folded into a running mean, because
/// the statistic that answers "how much health does this thing have" is a
/// quantile and a quantile cannot be maintained incrementally without keeping
/// the values. It is also the only form in which a bad estimate can be
/// explained after the fact.</para>
/// </summary>
/// <param name="Damage">Every player-side point that landed on it, not just this character's.</param>
public sealed record KillSample(
    string Mob,
    string Zone,
    int? Difficulty,
    string? TierName,
    long Damage,
    DateTime KilledAt);

/// <summary>How much to trust a number, in the terms the panel says it in.</summary>
public enum MobHealthConfidence
{
    /// <summary>Few kills, or kills that disagree wildly. A shape, not a number.</summary>
    Low,

    Medium,

    /// <summary>Enough consistent kills that the spread is the mob's, not the method's.</summary>
    High,
}

/// <summary>
/// The health reading for one mob in one place at one difficulty.
///
/// <para><paramref name="Health"/> is the median damage-to-kill, and it is
/// biased high by construction: the killing blow overshoots, so every sample is
/// the mob's health plus some overkill. It is reported as the headline anyway
/// because the number a player wants is "what does it take to drop this",
/// which is the biased one. <paramref name="Floor"/> and
/// <paramref name="Ceiling"/> carry the honest spread around it.</para>
/// </summary>
/// <param name="Floor">10th percentile — kills this cheap happened.</param>
/// <param name="Ceiling">90th percentile.</param>
/// <param name="Samples">Every kill on record for this key.</param>
/// <param name="CleanSamples">
/// Those that survived the concurrency filter — the ones the estimate is
/// actually computed from, unless there were too few and it fell back.
/// </param>
public sealed record MobHealthEstimate(
    string Mob,
    string Zone,
    int? Difficulty,
    string? TierName,
    long Health,
    long Floor,
    long Ceiling,
    int Samples,
    int CleanSamples,
    MobHealthConfidence Confidence,
    DateTime LastKilled);

/// <summary>
/// Learns how much health a mob has by watching what it takes to kill one
/// (F25). No external data and no server cooperation: a mob's health is the
/// damage it absorbs, and the log records every point of that.
///
/// <para><b>What identifies a mob.</b> The name alone does not. An instance's
/// difficulty rescales everything in it — the same froglok ton knight in The
/// City of Guk measures ~844 at tier 1 and ~1810 at tier 4 on this author's
/// log — so the key is (name, zone, difficulty). Difficulty comes off the zone
/// line, which is the one instance setting the client writes down; see
/// <see cref="Parsing.InstanceZone"/>.</para>
///
/// <para><b>What is missing.</b> Two of the three instance settings are never
/// logged: respawning vs non-respawning, and solo vs multiplayer. Neither can
/// be recovered, so neither is in the key, and if either scaled health the
/// buckets here would be silently mixing two populations. They appear not to:
/// across the keys with enough kills to tell, the damage-to-kill distributions
/// are unimodal with a right tail — one cluster plus overkill — rather than the
/// two humps mixing would produce. That is evidence, not proof, which is why
/// <see cref="MobHealthEstimate.Floor"/> and
/// <see cref="MobHealthEstimate.Ceiling"/> are reported beside every number
/// rather than the median being shown alone. A mob whose health really does
/// depend on something unlogged will say so as a wide band and a Low
/// confidence, which is the correct thing for it to say.</para>
///
/// <para><b>The concurrency filter.</b> Fights are keyed by NPC name, so two
/// mobs of the same name up at once are one fight (metrics §1). The first death
/// banks their combined damage and the survivor's remainder opens a fresh fight
/// that banks far too little — one sample too high and one too low, from a
/// single pull. They are detected by proximity: two kills on the same key
/// within <see cref="ConcurrencyWindow"/> are both discarded. Measured over the
/// 33 keys with 20+ kills in a 66 MB log, this cuts the median relative IQR
/// from 0.34 to 0.24 while keeping 71% of the samples. Stricter rules (drop a
/// kill whose successor re-engages within 10 s) reach 0.22 but throw away 60%
/// of the data, which is a bad trade for a feature whose whole problem is
/// having enough kills.</para>
/// </summary>
public sealed class MobHealthIndex
{
    /// <summary>
    /// Two kills of one name this close together are a pull that had two of
    /// them up, not two pulls. See the class doc for how this was chosen.
    /// </summary>
    public static readonly TimeSpan ConcurrencyWindow = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Below this the filter is skipped rather than applied: with three kills
    /// on record, discarding two to be careful leaves nothing to be careful
    /// with. The estimate falls back to every sample and reports Low.
    /// </summary>
    public const int MinCleanSamples = 4;

    /// <summary>
    /// Kills retained per key. Far more than any quantile needs, and it bounds
    /// the stored file — a camp worked for a week would otherwise accumulate
    /// thousands of identical samples. Oldest go first, so a server that
    /// rebalances a zone is followed rather than averaged with its own past.
    /// </summary>
    public const int SamplesPerMob = 200;

    private readonly Dictionary<MobKey, List<KillSample>> _samples = [];

    /// <summary>Distinct (mob, zone, difficulty) keys on record.</summary>
    public int KeyCount => _samples.Count;

    public int SampleCount => _samples.Values.Sum(v => v.Count);

    /// <summary>
    /// Reads the kills out of a fight list. A fight qualifies when it ended in
    /// a death, took damage, and happened somewhere known — a fight with no
    /// zone (the stretch of a log before its first zone line) is dropped rather
    /// than pooled, because pooling it would mix difficulties, which is the one
    /// error this whole feature exists to avoid.
    /// </summary>
    /// <param name="since">
    /// Only kills after this instant, so a caller sweeping a growing fight list
    /// every second does not rebuild the whole history each time.
    /// </param>
    public static List<KillSample> Harvest(IReadOnlyList<Fight> fights, DateTime since = default)
    {
        var samples = new List<KillSample>();
        foreach (var fight in fights)
        {
            if (!fight.Dead || !fight.HasDamage || fight.DamageTotal <= 0)
            {
                continue;
            }

            if (fight.LastDamageTime <= since)
            {
                continue;
            }

            if (fight.Zone is not { BaseName.Length: > 0 } zone)
            {
                continue;
            }

            samples.Add(new KillSample(
                fight.Name, zone.BaseName, zone.Difficulty, zone.TierName,
                fight.DamageTotal, fight.LastDamageTime));
        }

        return samples;
    }

    /// <summary>All samples held, for persistence. Order is not meaningful.</summary>
    public List<KillSample> Snapshot() => _samples.Values.SelectMany(v => v).ToList();

    /// <summary>
    /// Merges kills in, ignoring ones already held. Re-opening a log replays
    /// every kill in it, so idempotency is the normal case rather than an edge
    /// one: a sample is identified by its key, its instant and its size, and
    /// two genuinely distinct kills agreeing on all three are indistinguishable
    /// anyway.
    /// </summary>
    /// <returns>How many were new.</returns>
    public int Add(IEnumerable<KillSample> samples)
    {
        var added = 0;
        foreach (var sample in samples)
        {
            var key = MobKey.Of(sample);
            if (!_samples.TryGetValue(key, out var list))
            {
                _samples[key] = list = [];
            }

            if (list.Any(existing =>
                    existing.KilledAt == sample.KilledAt && existing.Damage == sample.Damage))
            {
                continue;
            }

            list.Add(sample);
            added++;
        }

        if (added > 0)
        {
            Trim();
        }

        return added;
    }

    /// <summary>
    /// One estimate per key, <b>most recently killed first</b>.
    ///
    /// <para>Not best-known first, which is what this originally did. The index
    /// belongs to the server and accumulates for months, so ranking by
    /// confidence buries tonight's camp under every zone the account has ever
    /// worked — and buries it further the longer the app is used. Confidence is
    /// a column; it does not also need to be the order. Matches the attack
    /// profiles beside it (<see cref="MobAttackIndex.Estimates"/>).</para>
    ///
    /// <para>Keys with a single kill are included: one kill is a real
    /// observation and saying "about this much, from one fight" is more useful
    /// than saying nothing, so long as it is labelled Low.</para>
    /// </summary>
    public List<MobHealthEstimate> Estimates()
    {
        var results = new List<MobHealthEstimate>(_samples.Count);
        foreach (var (key, samples) in _samples)
        {
            results.Add(Estimate(key, samples));
        }

        return results
            .OrderByDescending(e => e.LastKilled)
            .ThenByDescending(e => e.CleanSamples)
            .ThenBy(e => e.Mob, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static MobHealthEstimate Estimate(MobKey key, List<KillSample> samples)
    {
        var ordered = samples.OrderBy(s => s.KilledAt).ToList();
        var clean = Clean(ordered);
        // Below the floor the filter costs more than it buys; fall back to
        // everything and let the confidence grade carry the warning.
        var used = clean.Count >= MinCleanSamples ? clean : ordered;

        var values = used.Select(s => s.Damage).OrderBy(v => v).ToList();
        var health = Percentile(values, 0.50);
        var floor = Percentile(values, 0.10);
        var ceiling = Percentile(values, 0.90);
        var latest = ordered[^1];

        // Names come off the latest sample, not the key: the key is folded to
        // lower case so that two spellings of one mob share a bucket, and
        // showing the folded form would put "a froglok ton knight" on screen
        // in a log that never wrote it that way.
        return new MobHealthEstimate(
            latest.Mob,
            latest.Zone,
            key.Difficulty,
            latest.TierName,
            health,
            floor,
            ceiling,
            ordered.Count,
            clean.Count,
            Grade(clean.Count, values, health),
            latest.KilledAt);
    }

    /// <summary>
    /// Drops kills that had a same-key neighbour inside
    /// <see cref="ConcurrencyWindow"/>. Both sides go: when one fight's total
    /// is inflated by a second mob, the second mob's own sample is deflated by
    /// exactly as much, and there is no way to tell from here which is which.
    /// </summary>
    private static List<KillSample> Clean(List<KillSample> ordered)
    {
        if (ordered.Count < 2)
        {
            return [.. ordered];
        }

        var suspect = new bool[ordered.Count];
        for (var i = 0; i < ordered.Count - 1; i++)
        {
            if (ordered[i + 1].KilledAt - ordered[i].KilledAt < ConcurrencyWindow)
            {
                suspect[i] = true;
                suspect[i + 1] = true;
            }
        }

        var kept = new List<KillSample>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!suspect[i])
            {
                kept.Add(ordered[i]);
            }
        }

        return kept;
    }

    /// <summary>
    /// How much the number is worth, from how many clean kills back it and how
    /// far they disagree. Spread is measured relative to the estimate because
    /// a ±400 spread means one thing on a 900-health mob and another on a
    /// 9,000-health one.
    /// </summary>
    private static MobHealthConfidence Grade(int cleanCount, List<long> values, long health)
    {
        var spread = health <= 0
            ? double.PositiveInfinity
            : (double)(Percentile(values, 0.75) - Percentile(values, 0.25)) / health;

        if (cleanCount >= 10 && spread <= 0.25)
        {
            return MobHealthConfidence.High;
        }

        return cleanCount >= MinCleanSamples && spread <= 0.50
            ? MobHealthConfidence.Medium
            : MobHealthConfidence.Low;
    }

    /// <summary>Nearest-rank percentile over an ascending list.</summary>
    private static long Percentile(List<long> ascending, double p)
    {
        if (ascending.Count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(p * ascending.Count) - 1;
        return ascending[Math.Clamp(rank, 0, ascending.Count - 1)];
    }

    private void Trim()
    {
        foreach (var list in _samples.Values)
        {
            if (list.Count <= SamplesPerMob)
            {
                continue;
            }

            list.Sort((a, b) => a.KilledAt.CompareTo(b.KilledAt));
            list.RemoveRange(0, list.Count - SamplesPerMob);
        }
    }

    /// <summary>
    /// What counts as the same mob. Names and zones are compared
    /// case-insensitively because the log's own grammars disagree about the
    /// leading article's case ("a bandit" in loot, "A bandit" in deaths) —
    /// the same inconsistency the loot view already works around.
    /// </summary>
    private readonly record struct MobKey(string Mob, string Zone, int? Difficulty)
    {
        public static MobKey Of(KillSample sample) =>
            new(sample.Mob.ToLowerInvariant(), sample.Zone.ToLowerInvariant(), sample.Difficulty);
    }
}
