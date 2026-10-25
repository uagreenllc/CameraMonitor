using CameraMonitor.Api.Features.Archive;
using CameraMonitor.Api.Infrastructure.Db;
using FluentAssertions;

namespace CameraMonitor.Tests.Archive;

public sealed class RetentionPlannerTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly DateTime Now = new(2026, 10, 25, 14, 0, 0, DateTimeKind.Utc);

    private static SegmentRow Row(TimeSpan age, long bytes = 1000) =>
        new() { CameraId = "a", LensId = "b", StartUtc = Now - age, DurationMs = 10000, Path = $"rec/a/b/{age.Ticks}.ts", Bytes = bytes };

    private static RetentionInput Input(IReadOnlyList<SegmentRow> rows, long free, long minFree = 0) =>
        new(rows, Now, TimeSpan.FromDays(14), free, minFree);

    [Fact]
    public void Deletes_segments_older_than_retention()
    {
        var rows = new[] { Row(TimeSpan.FromDays(20)), Row(TimeSpan.FromDays(15)), Row(TimeSpan.FromDays(1)) };
        RetentionPlanner.Plan(Input(rows, 500 * Gb)).Should().Equal(rows[0], rows[1]);
    }

    [Fact]
    public void Deletes_oldest_until_free_space_met()
    {
        var rows = Enumerable.Range(2, 5).Reverse().Select(d => Row(TimeSpan.FromDays(d), Gb)).ToArray();
        RetentionPlanner.Plan(Input(rows, Gb, 3 * Gb)).Should().Equal(rows[0], rows[1]);
    }

    [Fact]
    public void Keeps_segments_younger_than_1h_even_when_disk_full()
    {
        var rows = new[] { Row(TimeSpan.FromMinutes(30)), Row(TimeSpan.FromMinutes(20)) };
        RetentionPlanner.Plan(Input(rows, 0, 100 * Gb)).Should().BeEmpty();
    }

    [Fact]
    public void Nothing_to_do_returns_empty() =>
        RetentionPlanner.Plan(Input([Row(TimeSpan.FromDays(1))], 500 * Gb, 100 * Gb)).Should().BeEmpty();
}
