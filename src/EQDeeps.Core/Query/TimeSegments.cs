namespace EQDeeps.Core.Query;

/// <summary>
/// A union of inclusive [begin, end] second ranges with overlap/adjacency
/// merging — the active-time primitive behind every DPS denominator (metrics
/// doc §4). Two fights that overlap in wall-clock time never double-count a
/// second.
/// </summary>
public sealed class TimeSegments
{
    private readonly List<TimeRange> _segments = [];

    public IReadOnlyList<TimeRange> Segments => _segments;

    public double TotalSeconds
    {
        get
        {
            double total = 0;
            foreach (var segment in _segments)
            {
                total += segment.TotalSeconds;
            }

            return total;
        }
    }

    public void Add(DateTime begin, DateTime end)
    {
        if (end < begin)
        {
            (begin, end) = (end, begin);
        }

        // Find the insertion window: segments that overlap or touch [begin-1s, end+1s].
        var index = 0;
        while (index < _segments.Count && _segments[index].End < begin.AddSeconds(-1))
        {
            index++;
        }

        var mergeEnd = index;
        while (mergeEnd < _segments.Count && _segments[mergeEnd].Begin <= end.AddSeconds(1))
        {
            if (_segments[mergeEnd].Begin < begin)
            {
                begin = _segments[mergeEnd].Begin;
            }

            if (_segments[mergeEnd].End > end)
            {
                end = _segments[mergeEnd].End;
            }

            mergeEnd++;
        }

        _segments.RemoveRange(index, mergeEnd - index);
        _segments.Insert(index, new TimeRange(begin, end));
    }

    public void AddAll(TimeSegments other)
    {
        foreach (var segment in other._segments)
        {
            Add(segment.Begin, segment.End);
        }
    }

    /// <summary>
    /// Applies a selection trim over the virtual concatenated timeline: skip the
    /// first <paramref name="skipSeconds"/>, keep at most <paramref name="maxSeconds"/>.
    /// </summary>
    public TimeSegments Trim(int skipSeconds, int? maxSeconds)
    {
        if (skipSeconds <= 0 && maxSeconds is null)
        {
            return this;
        }

        var result = new TimeSegments();
        double skip = skipSeconds;
        var remaining = maxSeconds.HasValue ? (double)maxSeconds.Value : double.PositiveInfinity;
        foreach (var segment in _segments)
        {
            var length = segment.TotalSeconds;
            var begin = segment.Begin;
            if (skip > 0)
            {
                if (skip >= length)
                {
                    skip -= length;
                    continue;
                }

                begin = begin.AddSeconds(skip);
                length -= skip;
                skip = 0;
            }

            if (remaining <= 0)
            {
                break;
            }

            var take = Math.Min(length, remaining);
            result.Add(begin, begin.AddSeconds(take - 1));
            remaining -= take;
        }

        return result;
    }

    /// <summary>Intersects one range with this union; empty list when disjoint.</summary>
    public List<TimeRange> Intersect(TimeRange range)
    {
        var result = new List<TimeRange>();
        foreach (var segment in _segments)
        {
            var begin = segment.Begin > range.Begin ? segment.Begin : range.Begin;
            var end = segment.End < range.End ? segment.End : range.End;
            if (begin <= end)
            {
                result.Add(new TimeRange(begin, end));
            }
        }

        return result;
    }
}
