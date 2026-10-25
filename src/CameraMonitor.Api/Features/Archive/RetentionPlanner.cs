using CameraMonitor.Api.Infrastructure.Db;

namespace CameraMonitor.Api.Features.Archive;

public sealed record RetentionInput(IReadOnlyList<SegmentRow> OldestFirst, DateTime NowUtc, TimeSpan Retention, long FreeBytes, long MinFreeBytes);

public static class RetentionPlanner
{
    public static readonly TimeSpan NeverDeleteYoungerThan = TimeSpan.FromHours(1);

    public static IReadOnlyList<SegmentRow> Plan(RetentionInput input)
    {
        var result = new List<SegmentRow>();
        long reclaimed = 0;
        foreach (var s in input.OldestFirst)
        {
            var age = input.NowUtc - s.StartUtc;
            if (age < NeverDeleteYoungerThan) break;
            if (age > input.Retention || input.FreeBytes < input.MinFreeBytes - reclaimed)  // subtract, don't add: FreeBytes may be long.MaxValue
            {
                result.Add(s);
                reclaimed += s.Bytes;
            }
            else break; // oldest-first: nothing later is older, and the free-space need is already met
        }
        return result;
    }
}
