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
/// <para><b>On EQ Legends a character is several levels at once.</b> Class
/// loadouts level independently and swapping between them produces no log line
/// whatsoever — the same silence F24 hits on gear, and confirmed by grepping a
/// 690,000-line log where every occurrence of "loadout" is a player typing it
/// in chat. So one log dings to 41, then to 11 an hour later, then back up:
/// that is not a de-level, it is a different class being played by the same
/// person, and both readings are true at the same time.</para>
///
/// <para>This is why "the level at instant t" is the last level <i>announced</i>
/// and nothing better. A swap is invisible, so fights between a swap and the
/// next ding on the new loadout are attributed to the loadout that was put
/// away. Nothing here can fix that; a /who typed after a swap is what corrects
/// it, and the numbers land in the right rows from then on. Downstream this
/// axis is doing double duty as a <i>loadout</i> axis, which is the right
/// answer for the wrong-looking reason: a different loadout is a different
/// class with different mitigation, and its numbers belong apart.</para>
///
/// <para>The other gap is a genuine de-level, which the client also does not
/// log. A /who read backwards across either reports the level the player ended
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
