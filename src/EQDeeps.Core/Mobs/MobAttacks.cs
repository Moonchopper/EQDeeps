using EQDeeps.Core.Events;
using EQDeeps.Core.Sessions;

namespace EQDeeps.Core.Mobs;

/// <summary>
/// What one mob's attacks did to one defender over one fight — the unit the
/// index folds in and the unit idempotency is decided on.
/// </summary>
/// <param name="FightBegin">
/// Identifies the fight. Re-opening a log replays every fight in it, so the
/// index needs to recognize one it has already counted; a fight's start instant
/// is the only thing about it that is stable across replays.
/// </param>
/// <param name="DefenderLevel">
/// The defender's level when this happened, or null when the log never said.
/// See <see cref="DefenderLevels"/> for where it comes from and why unknown is
/// a bucket rather than a guess.
/// </param>
public sealed record AttackSample(
    string Mob,
    string Zone,
    int? Difficulty,
    string? TierName,
    int? DefenderLevel,
    string Defender,
    DateTime FightBegin,
    DateTime FightEnd,
    Dictionary<string, SkillTally> BySkill);

/// <summary>
/// The counters for one attack of one mob: how often it was thrown, how it was
/// answered, and how hard it landed.
///
/// <para><b>Ripostes are missing from <see cref="Swings"/> and every rate
/// derived from it</b>, and there is no fixing it from here. A swing the
/// defender riposted is written as the defender's own counter-attack line and
/// the attempt itself records nothing (see
/// <see cref="Parsing.DamageParser"/>), so the honest reading of a rate here is
/// "of the swings the log accounted for", not "of the swings thrown".</para>
/// </summary>
public sealed class SkillTally
{
    /// <summary>Melee attempts the log accounted for. Spells never count as swings.</summary>
    public int Swings { get; set; }

    /// <summary>Attempts that did damage.</summary>
    public int Landed { get; set; }

    public int Misses { get; set; }

    public int Dodges { get; set; }

    public int Parries { get; set; }

    public int Blocks { get; set; }

    public int Absorbs { get; set; }

    public int Invulnerable { get; set; }

    public long Total { get; set; }

    public long MaxHit { get; set; }

    public long MinHit { get; set; }

    /// <summary>
    /// Landed hit sizes, as bucket index → count (see
    /// <see cref="HitHistogram"/>). Sparse because a mob's hits cluster into a
    /// dozen buckets out of the eighty the scale offers, and a dictionary of
    /// the ones it used costs a fraction of an array of the ones it did not.
    /// </summary>
    public Dictionary<int, int> Histogram { get; set; } = [];

    /// <summary>True for spell/DoT damage, which has attempts but no swing accounting.</summary>
    public bool Spell { get; set; }

    public void Absorb(SkillTally other)
    {
        Swings += other.Swings;
        Landed += other.Landed;
        Misses += other.Misses;
        Dodges += other.Dodges;
        Parries += other.Parries;
        Blocks += other.Blocks;
        Absorbs += other.Absorbs;
        Invulnerable += other.Invulnerable;
        Total += other.Total;
        MaxHit = Math.Max(MaxHit, other.MaxHit);
        MinHit = MinHit == 0 ? other.MinHit : Math.Min(MinHit, other.MinHit);
        Spell |= other.Spell;
        foreach (var (bucket, count) in other.Histogram)
        {
            Histogram[bucket] = Histogram.GetValueOrDefault(bucket) + count;
        }
    }

    public void Record(long amount)
    {
        Landed++;
        Total += amount;
        MaxHit = Math.Max(MaxHit, amount);
        MinHit = MinHit == 0 ? amount : Math.Min(MinHit, amount);
        var bucket = HitHistogram.BucketOf(amount);
        Histogram[bucket] = Histogram.GetValueOrDefault(bucket) + 1;
    }
}

/// <summary>
/// Log-spaced buckets for hit sizes, four to the doubling.
///
/// <para>Hits are three to four orders of magnitude more numerous than kills,
/// so the trick F25 uses — keep every sample, because a quantile needs them —
/// does not survive contact with a tanking log. A histogram gets the quantiles
/// back in bounded space, and log spacing is what makes one scale work for a
/// 12-point rat and a 4,000-point raid boss: every bucket is about 19% wide, so
/// the resolution is proportional at both ends rather than fine at one and
/// useless at the other.</para>
/// </summary>
public static class HitHistogram
{
    /// <summary>Buckets per doubling. Four gives ~19% width — finer than the estimate deserves.</summary>
    public const int PerOctave = 4;

    /// <summary>Covers 1 → ~1.05 million, past any hit EverQuest has printed.</summary>
    public const int Buckets = 20 * PerOctave;

    public static int BucketOf(long amount)
    {
        if (amount <= 1)
        {
            return 0;
        }

        var index = (int)(Math.Log2(amount) * PerOctave);
        return Math.Clamp(index, 0, Buckets - 1);
    }

    /// <summary>The middle of a bucket, which is the best a bucketed value can say.</summary>
    public static long ValueOf(int bucket) =>
        (long)Math.Round(Math.Pow(2, (bucket + 0.5) / PerOctave));

    /// <summary>Nearest-rank quantile over a sparse histogram. Zero when empty.</summary>
    public static long Quantile(Dictionary<int, int> histogram, double p)
    {
        var total = 0L;
        foreach (var count in histogram.Values)
        {
            total += count;
        }

        if (total == 0)
        {
            return 0;
        }

        var rank = (long)Math.Ceiling(p * total);
        var seen = 0L;
        foreach (var bucket in histogram.Keys.Order())
        {
            seen += histogram[bucket];
            if (seen >= rank)
            {
                return ValueOf(bucket);
            }
        }

        return ValueOf(histogram.Keys.Max());
    }
}

/// <summary>How much to trust a profile, in the terms the panel says it in.</summary>
public enum MobAttackConfidence
{
    /// <summary>Too few landed hits, or nobody knows who was being hit.</summary>
    Low,

    Medium,

    /// <summary>Enough hits on a defender of known level that the numbers are the mob's.</summary>
    High,
}

/// <summary>One of a mob's attacks, as the panel reads it.</summary>
public sealed record MobAttackSkill(
    string Skill,
    bool Spell,
    int Swings,
    int Landed,
    long Total,
    double AvgHit,
    long MedianHit,
    long Floor,
    long Ceiling,
    long MaxHit,
    long MinHit,
    double HitRate,
    double MissRate,
    double AvoidRate);

/// <summary>
/// What it costs to stand in front of one mob, in one place, at one difficulty,
/// at one defender level.
///
/// <para><b>The headline hit-size figures are melee only.</b> A real log made
/// the case: a forsaken revenant lands 209 punches averaging 66, 752 damage
/// shield ticks averaging 15, and four Shocks of Swords averaging 582. Pooling
/// those gives "average hit 35", a number describing none of the three and
/// dominated by the one the mob is not choosing to do. So
/// <paramref name="AvgHit"/>, <paramref name="MedianHit"/>,
/// <paramref name="Floor"/>, <paramref name="Ceiling"/>,
/// <paramref name="MaxHit"/> and <paramref name="MinHit"/> answer "how hard
/// does this thing swing", the rates answer "how often does it connect", and
/// spells and shields are in <paramref name="Total"/> and broken out in
/// <paramref name="Skills"/> where they can be read as themselves.</para>
///
/// <para>The p10–p90 band travels with the median rather than being collapsed
/// into one number. Unlike mob health, that spread is not doubt: a mob's melee
/// genuinely ranges over a wide band, and the band IS the answer.</para>
/// </summary>
/// <param name="Total">Every point this mob inflicted — melee, spells and shields together.</param>
/// <param name="Landed">Everything that did damage, on the same footing as <paramref name="Total"/>.</param>
/// <param name="MeleeHits">The landed swings the headline figures are computed from.</param>
/// <param name="SpellTotal">The rest of <paramref name="Total"/>: spells, DoTs and damage shields.</param>
/// <param name="Defenders">
/// Who the evidence came from, capped. Shown rather than hidden because this
/// key pools every defender at one level, and "these three people" and "these
/// thirty" are different claims about the same average.
/// </param>
public sealed record MobAttackEstimate(
    string Mob,
    string Zone,
    int? Difficulty,
    string? TierName,
    int? DefenderLevel,
    int Fights,
    int Swings,
    int Landed,
    long Total,
    int MeleeHits,
    long MeleeTotal,
    long SpellTotal,
    double AvgHit,
    long MedianHit,
    long Floor,
    long Ceiling,
    long MaxHit,
    long MinHit,
    double HitRate,
    double MissRate,
    double DodgeRate,
    double ParryRate,
    double BlockRate,
    double AbsorbRate,
    IReadOnlyList<string> Defenders,
    IReadOnlyList<MobAttackSkill> Skills,
    MobAttackConfidence Confidence,
    DateTime FirstSeen,
    DateTime LastSeen);

/// <summary>
/// What one key has accumulated. This is the persisted shape: a rolling tally
/// rather than a bag of samples, because hits arrive by the thousand where
/// kills arrive by the one.
/// </summary>
public sealed class MobAttackRecord
{
    public string Mob { get; set; } = string.Empty;

    public string Zone { get; set; } = string.Empty;

    public int? Difficulty { get; set; }

    public string? TierName { get; set; }

    public int? DefenderLevel { get; set; }

    /// <summary>
    /// Fight starts already folded in, as unix seconds, oldest first. This is
    /// the whole of the idempotency story: a replayed log offers the same
    /// fights again and they are recognized by when they began.
    /// </summary>
    public List<long> Fights { get; set; } = [];

    public List<string> Defenders { get; set; } = [];

    public Dictionary<string, SkillTally> BySkill { get; set; } = [];

    public DateTime FirstSeen { get; set; }

    public DateTime LastSeen { get; set; }
}

/// <summary>
/// Learns what a mob's attacks are worth by watching them land (F26) — the
/// mirror of <see cref="MobHealthIndex"/>, and deliberately not a copy of it.
///
/// <para><b>Why the key carries the defender's level.</b> Mob health is a
/// property of the world: damage-to-death is the same number whoever deals it,
/// which is what lets F25 pool every character on a server into one estimate.
/// How hard a mob hits is not that. It is a fact about a pairing — the mob's
/// offense against a particular defender's mitigation — and pooling a level-40
/// character's incoming damage with a level-60's would produce an average
/// describing neither. So the key is (mob, zone, difficulty, defender level),
/// and levels are kept exact rather than banded: EQ mitigation moves per level,
/// and blurring five of them together is precisely the error the axis exists to
/// prevent.</para>
///
/// <para><b>Unknown is a bucket, never a guess.</b> The log states the owner's
/// level (dings, and a self-/who, which is better because it observes rather
/// than infers) but says nothing about anyone else's unless a /who caught them
/// unanonymous. Records whose defender level was never established key to null
/// and are labelled as such. They are not dropped — a group's tank taking hits
/// is real evidence about the mob — and they are not folded into the owner's
/// level either, which would be inventing the one thing that was not
/// observed.</para>
///
/// <para><b>Why no concurrency filter.</b> F25 discards kills that look like
/// two mobs of one name, because a fight keyed by name banks both their damage
/// into one total. That failure does not exist here: this measures the size and
/// outcome of individual swings, and two identical mobs swinging are drawn from
/// one distribution. Twice the evidence, same answer.</para>
///
/// <para><b>Why confidence is not graded on spread.</b> F25 reads a wide spread
/// as the method failing, since a mob has one health. A mob's melee has a real
/// range — often four-fold from minimum to maximum — so grading it that way
/// would mark the most honest rows as the least trustworthy. Confidence here is
/// graded on how much evidence backs the numbers and whether the defender's
/// level was known at all, which are the two ways this measurement actually
/// goes wrong.</para>
/// </summary>
public sealed class MobAttackIndex
{
    /// <summary>
    /// Landed hits behind a High grade. Melee lands a few hundred times in a
    /// long evening at one camp, so this asks for a camp rather than a pull.
    /// </summary>
    public const int HighHits = 200;

    /// <summary>Below this the numbers are a shape, not a distribution.</summary>
    public const int MediumHits = 40;

    /// <summary>
    /// Fight starts remembered per key. Well past what any camp produces
    /// against one mob at one level, and it bounds the file: without a cap a
    /// server worked for a month would grow an idempotency list longer than the
    /// data it protects.
    /// </summary>
    public const int FightsPerKey = 500;

    /// <summary>Defender names kept per key — enough to say who, not a roster.</summary>
    public const int DefendersPerKey = 8;

    private readonly Dictionary<MobAttackKey, MobAttackRecord> _records = [];

    /// <summary>Distinct (mob, zone, difficulty, defender level) keys on record.</summary>
    public int KeyCount => _records.Count;

    public int FightCount => _records.Values.Sum(r => r.Fights.Count);

    public long LandedCount => _records.Values
        .Sum(r => (long)r.BySkill.Values.Sum(t => t.Landed));

    /// <summary>
    /// Reads a session's incoming hits out of its records, one sample per
    /// (fight, defender).
    ///
    /// <para>Only <b>closed</b> fights are read. A fight still in progress would
    /// be harvested again on the next sweep with a bigger total, and the second
    /// reading would be rejected as a duplicate of the first — banking the
    /// opening seconds of every fight and nothing after them. Waiting for the
    /// timeouts to have their say costs a few seconds of latency on a number
    /// nobody reads mid-swing.</para>
    /// </summary>
    /// <param name="since">
    /// Only fights that ended after this instant, so a sweep over a growing
    /// fight list does not re-walk the whole log every second.
    /// </param>
    public static List<AttackSample> Harvest(
        RecordStore records,
        IReadOnlyList<Fight> fights,
        IdentityRegistry identity,
        DefenderLevels levels,
        DateTime since = default)
    {
        var samples = new List<AttackSample>();
        foreach (var fight in fights)
        {
            if (!fight.Closed || fight.LastDamageTime <= since)
            {
                continue;
            }

            // A fight with no zone is dropped rather than pooled, for the same
            // reason F25 drops one: pooling it would mix difficulties, and a
            // difficulty rescales everything about the mob.
            if (fight.Zone is not { BaseName.Length: > 0 } zone)
            {
                continue;
            }

            var perDefender = new Dictionary<string, Dictionary<string, SkillTally>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var (_, evt) in records.Range(fight.BeginTime, fight.LastDamageTime))
            {
                if (evt is not DamageEvent damage ||
                    damage.Attacker is not { Length: > 0 } attacker ||
                    damage.AttackerIsSpell)
                {
                    continue;
                }

                // Only this fight's NPC, so a second mob up at the same time
                // contributes to its own key instead of this one's.
                if (!string.Equals(attacker, fight.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // "Not an NPC" rather than "a verified player", matching what
                // the tanking source counts. Most group members never say a
                // word, join a raid or turn up in a /who, so demanding
                // verification would quietly drop the defenders a tank most
                // wants to compare against — while the article-and-spaces test
                // still keeps a mob-on-mob swing out.
                if (identity.IsDefinitelyNpc(damage.Defender))
                {
                    continue;
                }

                if (!perDefender.TryGetValue(damage.Defender, out var bySkill))
                {
                    perDefender[damage.Defender] = bySkill = [];
                }

                Apply(bySkill, damage);
            }

            foreach (var (defender, bySkill) in perDefender)
            {
                if (bySkill.Count == 0)
                {
                    continue;
                }

                samples.Add(new AttackSample(
                    fight.Name, zone.BaseName, zone.Difficulty, zone.TierName,
                    levels.LevelOf(defender, fight.BeginTime), defender,
                    fight.BeginTime, fight.LastDamageTime, bySkill));
            }
        }

        return samples;
    }

    /// <summary>
    /// Folds one damage record into a defender's per-skill tallies. Melee
    /// attempts count as swings whatever their outcome; spell damage does not,
    /// because "60% of its Ancient Breaths landed" is not a thing anyone can
    /// dodge and a hit rate over a denominator like that would be a lie with a
    /// percent sign on it.
    /// </summary>
    private static void Apply(Dictionary<string, SkillTally> bySkill, DamageEvent damage)
    {
        var spell = damage.Kind is DamageKind.DirectDamage or DamageKind.DamageOverTime
            or DamageKind.DamageShield or DamageKind.Other;
        var skill = damage.SubType is { Length: > 0 } sub ? sub : Unnamed(damage.Kind);
        if (!bySkill.TryGetValue(skill, out var tally))
        {
            bySkill[skill] = tally = new SkillTally { Spell = spell };
        }

        if (!spell)
        {
            tally.Swings++;
        }

        switch (damage.Kind)
        {
            case DamageKind.Miss:
                tally.Misses++;
                break;
            case DamageKind.Dodge:
                tally.Dodges++;
                break;
            case DamageKind.Parry:
                tally.Parries++;
                break;
            case DamageKind.Block:
                tally.Blocks++;
                break;
            case DamageKind.Absorb:
                tally.Absorbs++;
                break;
            case DamageKind.Invulnerable:
                tally.Invulnerable++;
                break;
            default:
                if (damage.Amount > 0)
                {
                    tally.Record(damage.Amount);
                }

                break;
        }
    }

    /// <summary>
    /// What to call an attack the line gave no name to. These reach the panel
    /// as-is, so they are written the way a person writes them rather than the
    /// way the enum spells them — "Damage shield", not "DamageShield".
    /// </summary>
    private static string Unnamed(DamageKind kind) => kind switch
    {
        DamageKind.DamageShield => "Damage shield",
        DamageKind.DirectDamage => "Spell",
        DamageKind.DamageOverTime => "Damage over time",
        DamageKind.Melee => "Melee",
        _ => "Other",
    };

    /// <summary>All keys held, for persistence. Order is not meaningful.</summary>
    public List<MobAttackRecord> Snapshot() => [.. _records.Values];

    /// <summary>Reloads from persisted records, replacing whatever is held.</summary>
    public void Load(IEnumerable<MobAttackRecord> records)
    {
        _records.Clear();
        foreach (var record in records)
        {
            _records[MobAttackKey.Of(record)] = record;
        }
    }

    /// <summary>
    /// Merges samples in, ignoring fights already counted. Re-opening a log
    /// replays every fight in it, so this is the normal path rather than an
    /// edge case.
    /// </summary>
    /// <returns>How many (fight, defender) samples were new.</returns>
    public int Add(IEnumerable<AttackSample> samples)
    {
        var added = 0;
        foreach (var sample in samples)
        {
            var key = MobAttackKey.Of(sample);
            if (!_records.TryGetValue(key, out var record))
            {
                _records[key] = record = new MobAttackRecord
                {
                    Mob = sample.Mob,
                    Zone = sample.Zone,
                    Difficulty = sample.Difficulty,
                    TierName = sample.TierName,
                    DefenderLevel = sample.DefenderLevel,
                    FirstSeen = sample.FightBegin,
                };
            }

            var stamp = ToStamp(sample.FightBegin);
            var at = record.Fights.BinarySearch(stamp);
            if (at >= 0)
            {
                continue;
            }

            // A fight older than the oldest one still remembered cannot be
            // distinguished from one that fell off the end of the list, so it
            // is treated as already counted. Counting it again would inflate a
            // tally that can never be un-inflated; skipping it loses evidence
            // the log still holds and can offer again once the cap is raised.
            if (record.Fights.Count >= FightsPerKey && stamp < record.Fights[0])
            {
                continue;
            }

            record.Fights.Insert(~at, stamp);
            if (record.Fights.Count > FightsPerKey)
            {
                record.Fights.RemoveAt(0);
            }

            foreach (var (skill, tally) in sample.BySkill)
            {
                if (!record.BySkill.TryGetValue(skill, out var running))
                {
                    record.BySkill[skill] = running = new SkillTally();
                }

                running.Absorb(tally);
            }

            if (record.Defenders.Count < DefendersPerKey &&
                !record.Defenders.Contains(sample.Defender, StringComparer.OrdinalIgnoreCase))
            {
                record.Defenders.Add(sample.Defender);
            }

            if (sample.FightBegin < record.FirstSeen || record.FirstSeen == default)
            {
                record.FirstSeen = sample.FightBegin;
            }

            if (sample.FightBegin > record.LastSeen)
            {
                record.LastSeen = sample.FightBegin;
                // Names and tier words come off the newest sample rather than
                // the key, which is folded to lower case so two spellings share
                // a bucket. Showing the folded form would put a mob on screen
                // in a spelling its log never used.
                record.Mob = sample.Mob;
                record.Zone = sample.Zone;
                record.TierName = sample.TierName;
            }

            added++;
        }

        return added;
    }

    /// <summary>
    /// One profile per key, best-evidenced first. Keys with a single fight are
    /// included and labelled Low: one evening against a mob is a real
    /// observation, and "hits about this hard, from one fight" beats silence.
    /// </summary>
    public List<MobAttackEstimate> Estimates()
    {
        var results = new List<MobAttackEstimate>(_records.Count);
        foreach (var record in _records.Values)
        {
            results.Add(Estimate(record));
        }

        return results
            .OrderByDescending(e => e.Confidence)
            .ThenByDescending(e => e.Landed)
            .ThenBy(e => e.Mob, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static MobAttackEstimate Estimate(MobAttackRecord record)
    {
        var all = new SkillTally();
        var melee = new SkillTally();
        foreach (var tally in record.BySkill.Values)
        {
            all.Absorb(tally);
            if (!tally.Spell)
            {
                melee.Absorb(tally);
            }
        }

        var skills = record.BySkill
            .Select(pair => Skill(pair.Key, pair.Value))
            .OrderByDescending(s => s.Total)
            .ToList();

        return new MobAttackEstimate(
            record.Mob,
            record.Zone,
            record.Difficulty,
            record.TierName,
            record.DefenderLevel,
            record.Fights.Count,
            all.Swings,
            all.Landed,
            all.Total,
            melee.Landed,
            melee.Total,
            all.Total - melee.Total,
            // Melee only, from here to the rates: see the record's doc for the
            // log that made the case against averaging a damage shield with a
            // backstab.
            Ratio(melee.Total, melee.Landed),
            HitHistogram.Quantile(melee.Histogram, 0.50),
            HitHistogram.Quantile(melee.Histogram, 0.10),
            HitHistogram.Quantile(melee.Histogram, 0.90),
            melee.MaxHit,
            melee.MinHit,
            // Every rate is over MELEE swings, not over everything the mob did:
            // spells have no attempt to avoid, and letting them into the
            // denominator would make a caster look evasion-proof.
            Percent(melee.Landed, melee.Swings),
            Percent(melee.Misses, melee.Swings),
            Percent(melee.Dodges, melee.Swings),
            Percent(melee.Parries, melee.Swings),
            Percent(melee.Blocks, melee.Swings),
            Percent(melee.Absorbs + melee.Invulnerable, melee.Swings),
            record.Defenders,
            skills,
            // Graded on the melee that backs the headline, not on every tick of
            // a damage shield — a thousand shield procs and four swings is not
            // a thousand observations of how hard the thing hits.
            Grade(melee.Landed, record.DefenderLevel),
            record.FirstSeen,
            record.LastSeen);
    }

    private static MobAttackSkill Skill(string name, SkillTally tally) =>
        new(name,
            tally.Spell,
            tally.Swings,
            tally.Landed,
            tally.Total,
            Ratio(tally.Total, tally.Landed),
            HitHistogram.Quantile(tally.Histogram, 0.50),
            HitHistogram.Quantile(tally.Histogram, 0.10),
            HitHistogram.Quantile(tally.Histogram, 0.90),
            tally.MaxHit,
            tally.MinHit,
            Percent(tally.Landed, tally.Swings),
            Percent(tally.Misses, tally.Swings),
            Percent(
                tally.Dodges + tally.Parries + tally.Blocks + tally.Absorbs + tally.Invulnerable,
                tally.Swings));

    /// <summary>
    /// How much the numbers are worth: how many hits back them, and whether the
    /// defender they were measured against is known. An unknown level caps the
    /// grade at Medium however much evidence there is, because volume cannot
    /// fix not knowing who was standing there — a thousand hits pooled across
    /// levels is a thousand hits describing nobody.
    /// </summary>
    private static MobAttackConfidence Grade(int landed, int? defenderLevel)
    {
        if (defenderLevel is null)
        {
            return landed >= HighHits ? MobAttackConfidence.Medium : MobAttackConfidence.Low;
        }

        if (landed >= HighHits)
        {
            return MobAttackConfidence.High;
        }

        return landed >= MediumHits ? MobAttackConfidence.Medium : MobAttackConfidence.Low;
    }

    /// <summary>Whole seconds, which is all a log line's timestamp carries anyway.</summary>
    private static long ToStamp(DateTime instant) =>
        (long)(instant - DateTime.UnixEpoch).TotalSeconds;

    private static double Ratio(double numerator, double denominator) =>
        denominator > 0 ? numerator / denominator : 0;

    private static double Percent(double numerator, double denominator) =>
        denominator > 0 ? numerator / denominator * 100 : 0;

    /// <summary>
    /// What counts as the same matchup. Names and zones fold to lower case for
    /// the same reason <see cref="MobHealthIndex"/> folds them: the log's own
    /// grammars disagree about the leading article's case.
    /// </summary>
    private readonly record struct MobAttackKey(
        string Mob, string Zone, int? Difficulty, int? DefenderLevel)
    {
        public static MobAttackKey Of(AttackSample sample) =>
            new(sample.Mob.ToLowerInvariant(), sample.Zone.ToLowerInvariant(),
                sample.Difficulty, sample.DefenderLevel);

        public static MobAttackKey Of(MobAttackRecord record) =>
            new(record.Mob.ToLowerInvariant(), record.Zone.ToLowerInvariant(),
                record.Difficulty, record.DefenderLevel);
    }
}
