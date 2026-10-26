namespace CameraMonitor.Api.Features.Backfill;

public sealed record Gap(DateTime FromUtc, DateTime ToUtc);

public static class GapFinder
{
    /// <summary>
    /// Holes longer than <paramref name="minGap"/> in the window not covered by any segment, newest first
    /// (the card overwrites the oldest recordings first, so the newest gaps are the ones still recoverable).
    /// </summary>
    public static IReadOnlyList<Gap> Find(IEnumerable<(DateTime StartUtc, int DurationMs)> orderedByStart, DateTime windowFromUtc, DateTime windowToUtc, TimeSpan minGap)
    {
        var gaps = new List<Gap>();
        var covered = windowFromUtc;   // everything before this is recorded (or outside the window)
        foreach (var (start, ms) in orderedByStart)
        {
            var holeEnd = start < windowToUtc ? start : windowToUtc;
            if (holeEnd - covered > minGap)
            {
                gaps.Add(new Gap(covered, holeEnd));
            }

            var end = start.AddMilliseconds(ms);
            if (end > covered)
            {
                covered = end;
            }

            if (covered >= windowToUtc)
            {
                break;
            }
        }

        if (windowToUtc - covered > minGap)
        {
            gaps.Add(new Gap(covered, windowToUtc));
        }

        gaps.Reverse();
        return gaps;
    }
}
