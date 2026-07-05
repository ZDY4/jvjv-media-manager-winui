using JvJvMediaManager.Services;

namespace JvJvMediaManager.Controllers.MainPage;

internal static class ClipSegmentEditor
{
    public static List<VideoClipSegment> NormalizeSegments(IEnumerable<VideoClipSegment> segments, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return new List<VideoClipSegment>();
        }

        var normalized = segments
            .Where(segment => segment != null)
            .Select(segment => new VideoClipSegment
            {
                Start = ClampToRange(segment.Start, TimeSpan.Zero, duration),
                End = ClampToRange(segment.End, TimeSpan.Zero, duration)
            })
            .Where(segment => segment.End > segment.Start)
            .OrderBy(segment => segment.Start)
            .ThenBy(segment => segment.End)
            .ToList();

        if (normalized.Count == 0)
        {
            return normalized;
        }

        var merged = new List<VideoClipSegment> { normalized[0] };
        for (var i = 1; i < normalized.Count; i++)
        {
            var current = normalized[i];
            var previous = merged[^1];
            if (current.Start <= previous.End)
            {
                merged[^1] = new VideoClipSegment
                {
                    Start = previous.Start,
                    End = Max(previous.End, current.End)
                };
                continue;
            }

            merged.Add(current);
        }

        return merged;
    }

    public static List<VideoClipSegment> InvertSegments(IReadOnlyList<VideoClipSegment> segments, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return new List<VideoClipSegment>();
        }

        var keptSegments = new List<VideoClipSegment>();
        var cursor = TimeSpan.Zero;
        foreach (var segment in NormalizeSegments(segments, duration))
        {
            if (segment.Start > cursor)
            {
                keptSegments.Add(new VideoClipSegment
                {
                    Start = cursor,
                    End = segment.Start
                });
            }

            cursor = Max(cursor, segment.End);
        }

        if (cursor < duration)
        {
            keptSegments.Add(new VideoClipSegment
            {
                Start = cursor,
                End = duration
            });
        }

        return keptSegments
            .Where(segment => segment.End > segment.Start)
            .ToList();
    }

    public static int FindSegmentIndexContaining(TimeSpan position, IReadOnlyList<VideoClipSegment> segments)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (position >= segment.Start && position <= segment.End)
            {
                return i;
            }
        }

        return -1;
    }

    public static TimeSpan CalculateEffectiveOutputDuration(
        IReadOnlyList<VideoClipSegment> segments,
        TimeSpan totalDuration,
        VideoClipMode mode)
    {
        if (mode == VideoClipMode.Keep)
        {
            return segments.Aggregate(TimeSpan.Zero, (current, segment) => current + segment.Duration);
        }

        var removed = segments.Aggregate(TimeSpan.Zero, (current, segment) => current + segment.Duration);
        var remaining = totalDuration - removed;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    public static TimeSpan ClampToDuration(TimeSpan value, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return value > duration ? duration : value;
    }

    public static TimeSpan ClampToRange(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (max < min)
        {
            (min, max) = (max, min);
        }

        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    public static TimeSpan Max(TimeSpan first, TimeSpan second)
    {
        return first >= second ? first : second;
    }
}
