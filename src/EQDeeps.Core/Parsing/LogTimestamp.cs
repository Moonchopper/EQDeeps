namespace EQDeeps.Core.Parsing;

/// <summary>
/// Parses the fixed-width EverQuest log prefix: "[Day Mon DD HH:MM:SS YYYY] ".
/// The prefix is exactly 27 characters including the trailing space; the message
/// body starts at index 27. Validation is strict so the same routine can safely
/// probe for a second entry glitched onto the middle of a physical line without
/// false-splitting on chat text that happens to contain brackets.
/// </summary>
public static class LogTimestamp
{
    /// <summary>Length of the timestamp prefix including the trailing space.</summary>
    public const int PrefixLength = 27;

    /// <summary>
    /// Tries to parse a timestamp prefix at the start of <paramref name="line"/>.
    /// Timestamps are local time with no zone info; DST can make them go backwards,
    /// which is the caller's problem — this only validates shape and calendar range.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> line, out DateTime timestamp)
    {
        timestamp = default;
        if (line.Length < PrefixLength || line[0] != '[' || line[25] != ']' || line[26] != ' ')
        {
            return false;
        }

        // "[Sun Oct 08 20:07:10 2023] " — fixed positions throughout.
        if (line[4] != ' ' || line[8] != ' ' || line[11] != ' ' || line[14] != ':' || line[17] != ':' || line[20] != ' ')
        {
            return false;
        }

        if (!IsDayName(line.Slice(1, 3)))
        {
            return false;
        }

        var month = MonthNumber(line.Slice(5, 3));
        if (month == 0)
        {
            return false;
        }

        if (!TryTwoDigits(line, 9, out var day) ||
            !TryTwoDigits(line, 12, out var hour) ||
            !TryTwoDigits(line, 15, out var minute) ||
            !TryTwoDigits(line, 18, out var second) ||
            !TryFourDigits(line, 21, out var year))
        {
            return false;
        }

        if (day is < 1 or > 31 || hour > 23 || minute > 59 || second > 59 || year is < 1999 or > 2200)
        {
            return false;
        }

        if (day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        timestamp = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        return true;
    }

    private static bool IsDayName(ReadOnlySpan<char> s) =>
        s is "Sun" or "Mon" or "Tue" or "Wed" or "Thu" or "Fri" or "Sat";

    private static int MonthNumber(ReadOnlySpan<char> s) => s switch
    {
        "Jan" => 1,
        "Feb" => 2,
        "Mar" => 3,
        "Apr" => 4,
        "May" => 5,
        "Jun" => 6,
        "Jul" => 7,
        "Aug" => 8,
        "Sep" => 9,
        "Oct" => 10,
        "Nov" => 11,
        "Dec" => 12,
        _ => 0,
    };

    private static bool TryTwoDigits(ReadOnlySpan<char> s, int at, out int value)
    {
        value = 0;
        char a = s[at], b = s[at + 1];
        if (!char.IsAsciiDigit(a) || !char.IsAsciiDigit(b))
        {
            return false;
        }

        value = (a - '0') * 10 + (b - '0');
        return true;
    }

    private static bool TryFourDigits(ReadOnlySpan<char> s, int at, out int value)
    {
        value = 0;
        for (var i = 0; i < 4; i++)
        {
            var c = s[at + i];
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            value = value * 10 + (c - '0');
        }

        return true;
    }
}
