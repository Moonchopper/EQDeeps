namespace EQDeeps.Core.Spells;

/// <summary>
/// How long a buff lasts, from the two columns of <c>spells_us.txt</c> that
/// say so and the level of whoever cast it.
///
/// <para><b>How these columns were identified.</b> The file has 173 unlabelled
/// columns. Column 107 holds a small enum (values 0–13 plus sentinels, 37,920
/// of 73,963 spells at 0) and column 108 a much wider spread capped at 100,000
/// — the shape of a formula and a cap. That guess was then <i>checked against
/// the log</i>: pairing the landing and fade emotes this parser now resolves
/// (F10a) gives an observed duration for spells the owner actually received,
/// and for 13 of the 19 with enough pairs the formula below predicts the
/// observation to within a single 6-second tick — 1,641 s observed against
/// 1,620 predicted, 3,011 against 3,000, 2,169 against 2,160. Every one of the
/// six misses is observed *shorter* than predicted, which is what a re-buff,
/// a zone, a death or a dispel looks like. That is identification rather than
/// correlation, and it is why this file states the columns as fact.</para>
///
/// <para>Columns 11 and 12 carry the same pair of shapes and agree with
/// 107/108 on 82% of rows. Which of the two the client actually applies is
/// <b>not</b> settled here; 107/108 are used because they are the pair the
/// observations validate. If the other ever turns out to govern some case, the
/// evidence to redo this is a log and half an hour.</para>
///
/// <para>The formulas themselves are EverQuest's long-standing ones. They are
/// written out rather than table-driven so each line can be read against a
/// spell that proves it.</para>
/// </summary>
public static class SpellDuration
{
    /// <summary>A buff tick is six seconds, everywhere in EverQuest.</summary>
    public const int SecondsPerTick = 6;

    /// <summary>Formula 50 means "until something removes it" — a permanent illusion, a mount.</summary>
    public const int PermanentFormula = 50;

    /// <summary>Ticks a buff lasts, or 0 for an instant spell, or null when it does not expire on its own.</summary>
    public static int? Ticks(int formula, int cap, int casterLevel)
    {
        if (formula == PermanentFormula)
        {
            return null;
        }

        var level = Math.Max(1, casterLevel);
        var value = formula switch
        {
            0 => 0,
            1 => level / 2,
            2 => (level / 2) + 5,
            3 => level * 30,
            4 => 50,
            5 => 2,
            6 => level / 2,
            7 => level,
            8 => level + 10,
            9 => (level * 2) + 10,
            10 => (level * 3) + 10,
            11 => cap,
            12 => level / 4,
            13 => cap,
            // 3600 is the file's "one hour" sentinel; anything else unknown
            // falls back to the cap, which is the value it would be clamped to
            // anyway and is never longer than the truth.
            3600 => 3600,
            _ => cap,
        };

        if (value <= 0)
        {
            return 0;
        }

        return cap > 0 ? Math.Min(value, cap) : value;
    }

    /// <summary>The same answer in seconds, for callers drawing a span on a clock.</summary>
    public static TimeSpan? Duration(int formula, int cap, int casterLevel) =>
        Ticks(formula, cap, casterLevel) is { } ticks
            ? TimeSpan.FromSeconds(ticks * SecondsPerTick)
            : null;
}
