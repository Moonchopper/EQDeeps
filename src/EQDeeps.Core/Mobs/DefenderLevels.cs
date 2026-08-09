using EQDeeps.Core.Events;
using EQDeeps.Core.Sessions;

namespace EQDeeps.Core.Mobs;

/// <summary>
/// What level someone was when a mob hit them (F26).
///
/// <para>This exists because how hard a mob hits is a fact about a pairing, not
/// about the mob — so <see cref="MobAttackIndex"/> keys on the defender's
/// level, and something has to establish it. The log is generous about the
/// owner and nearly silent about everyone else:</para>
///
/// <list type="bullet">
///   <item><b>Dings</b> ("Welcome to level 42!") fix the owner's level from
///   that moment. They are never read backwards: the level began there, and
///   before it the character was something lower.</item>
///   <item><b>/who lines</b> carry a level for every non-anonymous player in
///   the zone, the owner included. A /who <i>observes</i> a level that was
///   already true rather than announcing a change, so the first one read for a
///   name is read backwards over everything before it as well as forwards —
///   without which a player who types /who once at nine in the evening has no
///   level for the eight hours preceding, which is most of the log.</item>
/// </list>
///
/// <para>Anyone the log never levelled — most group members, every pet, every
/// anonymous player — resolves to null, and null is a bucket of its own rather
/// than a guess. Folding them into the owner's level would invent the single
/// thing that was not observed, and the panel would then report a confident
/// number about a defender it could not name.</para>
///
/// <para>The gap this cannot close is a de-level, which the client does not log
/// at all. A /who read backwards across one reports the level the player ended
/// on rather than the one they were — the same limitation
/// <see cref="Query.ContextTimeline"/> carries, for the same reason.</para>
/// </summary>
public sealed class DefenderLevels
{
    private readonly Dictionary<string, Observations> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    public static DefenderLevels Empty { get; } = new();

    /// <summary>Names the log ever established a level for.</summary>
    public int KnownCount => _byName.Count;

    /// <summary>
    /// Reads every level the log states, in one pass over the record stream.
    /// </summary>
    /// <param name="character">
    /// The log's owner — the one name a <see cref="LevelEvent"/> can belong to,
    /// since the client only ever announces its own dings.
    /// </param>
    public static DefenderLevels Build(RecordStore records, string character)
    {
        var levels = new DefenderLevels();
        for (var i = 0; i < records.Count; i++)
        {
            var (timestamp, evt) = records[i];
            switch (evt)
            {
                case LevelEvent ding:
                    levels.Observe(character, timestamp, ding.Level, backdatable: false);
                    break;

                case WhoEvent { Level: { } seen } who:
                    levels.Observe(who.Player, timestamp, seen, backdatable: true);
                    break;
            }
        }

        return levels;
    }

    /// <summary>
    /// The level this name was at <paramref name="at"/>, or null when the log
    /// never said. The last observation at or before the instant wins; failing
    /// that, the first observation is used only if it was a /who, which
    /// reported a level rather than announcing one.
    /// </summary>
    public int? LevelOf(string name, DateTime at)
    {
        if (!_byName.TryGetValue(name, out var seen))
        {
            return null;
        }

        var found = (int?)null;
        foreach (var (instant, level) in seen.Points)
        {
            if (instant > at)
            {
                break;
            }

            found = level;
        }

        if (found is not null)
        {
            return found;
        }

        return seen.FirstBackdatable ? seen.Points[0].Level : null;
    }

    private void Observe(string name, DateTime at, int level, bool backdatable)
    {
        if (string.IsNullOrEmpty(name) || level <= 0)
        {
            return;
        }

        if (!_byName.TryGetValue(name, out var seen))
        {
            _byName[name] = seen = new Observations { FirstBackdatable = backdatable };
        }

        // Repeating the level already in force is not an observation worth
        // keeping: a /who typed three times in a camp is one fact, not three.
        if (seen.Points.Count > 0 && seen.Points[^1].Level == level)
        {
            return;
        }

        seen.Points.Add((at, level));
    }

    /// <summary>
    /// One name's level history, in log order — which is time order, since the
    /// record stream is built that way.
    /// </summary>
    private sealed class Observations
    {
        public List<(DateTime Instant, int Level)> Points { get; } = [];

        /// <summary>Whether the earliest observation may be read backwards (a /who, not a ding).</summary>
        public bool FirstBackdatable { get; init; }
    }
}
