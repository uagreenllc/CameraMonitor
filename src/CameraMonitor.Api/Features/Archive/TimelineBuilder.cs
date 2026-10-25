using CameraMonitor.Api.Infrastructure.Db;

namespace CameraMonitor.Api.Features.Archive;

public sealed record TimeRange(DateTime FromUtc, DateTime ToUtc);

public static class TimelineBuilder
{
    public static IReadOnlyList<TimeRange> Merge(IEnumerable<SegmentRow> orderedByStart, TimeSpan maxGap)
    {
        var result = new List<TimeRange>();
        foreach (var s in orderedByStart)
        {
            var end = s.StartUtc.AddMilliseconds(s.DurationMs);
            if (result.Count > 0 && s.StartUtc - result[^1].ToUtc <= maxGap)
                result[^1] = result[^1] with { ToUtc = end > result[^1].ToUtc ? end : result[^1].ToUtc };
            else
                result.Add(new TimeRange(s.StartUtc, end));
        }
        return result;
    }
}
