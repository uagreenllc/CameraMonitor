using CameraMonitor.Api.Features.Backfill;

namespace CameraMonitor.Tests.Backfill;

public sealed class GapFinderTests
{
    private static readonly DateTime T = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(120);

    private static IEnumerable<(DateTime, int)> Segments(DateTime from, DateTime to)
    {
        for (var t = from; t < to; t = t.AddSeconds(10)) yield return (t, 10_000);
    }

    private static DateTime At(int h, int m) => new(2026, 7, 1, h, m, 0, DateTimeKind.Utc);

    [Fact]
    public void Contiguous_segments_have_no_gaps() =>
        GapFinder.Find(Segments(T, At(12, 30)), T, At(12, 30), MinGap).Should().BeEmpty();

    [Fact]
    public void Hole_longer_than_min_gap_is_reported() =>
        GapFinder.Find(Segments(T, At(12, 5)).Concat(Segments(At(12, 10), At(12, 30))), T, At(12, 30), MinGap)
            .Should().Equal(new Gap(At(12, 5), At(12, 10)));

    [Fact]
    public void Short_hole_is_ignored() =>
        GapFinder.Find(Segments(T, At(12, 5)).Concat(Segments(At(12, 5).AddSeconds(30), At(12, 30))), T, At(12, 30), MinGap)
            .Should().BeEmpty();

    [Fact]
    public void Leading_and_trailing_holes_use_the_window() =>
        GapFinder.Find(Segments(T, At(12, 30)), At(11, 50), At(12, 40), MinGap)
            .Should().Equal(new Gap(At(12, 30), At(12, 40)), new Gap(At(11, 50), T));

    [Fact]
    public void Empty_index_is_one_gap() =>
        GapFinder.Find([], T, At(12, 30), MinGap).Should().Equal(new Gap(T, At(12, 30)));
}
