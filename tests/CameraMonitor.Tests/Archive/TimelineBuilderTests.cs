using CameraMonitor.Api.Features.Archive;
using CameraMonitor.Api.Infrastructure.Db;
using FluentAssertions;

namespace CameraMonitor.Tests.Archive;

public sealed class TimelineBuilderTests
{
    private static readonly DateTime T = new(2026, 10, 25, 14, 0, 0, DateTimeKind.Utc);
    private static SegmentRow Seg(double offsetSec) => new() { CameraId = "a", LensId = "b", Path = "p", DurationMs = 10000, StartUtc = T.AddSeconds(offsetSec) };
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(1.5);

    [Fact]
    public void Contiguous_segments_make_one_range() =>
        TimelineBuilder.Merge([Seg(0), Seg(10), Seg(20)], Gap).Should().Equal(new TimeRange(T, T.AddSeconds(30)));

    [Fact]
    public void Five_second_gap_splits() =>
        TimelineBuilder.Merge([Seg(0), Seg(15)], Gap).Should().Equal(new TimeRange(T, T.AddSeconds(10)), new TimeRange(T.AddSeconds(15), T.AddSeconds(25)));

    [Fact]
    public void One_second_gap_does_not_split() =>
        TimelineBuilder.Merge([Seg(0), Seg(11)], Gap).Should().Equal(new TimeRange(T, T.AddSeconds(21)));
}
