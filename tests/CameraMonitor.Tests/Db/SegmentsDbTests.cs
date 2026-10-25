using CameraMonitor.Api.Infrastructure.Db;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CameraMonitor.Tests.Db;

public sealed class SegmentsDbTests
{
    private static SegmentRow Row(string cam, string lens, DateTime start) => new()
    {
        CameraId = cam,
        LensId = lens,
        StartUtc = start,
        DurationMs = 10_000,
        Path = $"rec/{cam}/{lens}/{start:yyyyMMdd}/{start:HH}/{start:HHmmss}.ts",
        Bytes = 1234,
    };

    [Fact]
    public async Task RoundTrip_and_range_query()
    {
        using var factory = new ApiFactory();
        var day = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Segments.AddRange(
                Row("a", "b", day), Row("a", "b", day.AddSeconds(10)), Row("a", "b", day.AddSeconds(20)),
                Row("a", "c", day.AddSeconds(10)));
            await db.SaveChangesAsync();
        }

        using var readScope = factory.Services.CreateScope();
        var read = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var from = day.AddSeconds(5);
        var to = day.AddSeconds(25);
        var rows = await read.Segments
            .Where(s => s.CameraId == "a" && s.LensId == "b" && s.StartUtc >= from && s.StartUtc < to)
            .OrderBy(s => s.StartUtc)
            .ToListAsync();

        rows.Select(r => r.StartUtc).Should().Equal(day.AddSeconds(10), day.AddSeconds(20));
        rows.Should().OnlyContain(r => r.StartUtc.Kind == DateTimeKind.Utc);
    }

    [Fact]
    public async Task Duplicate_path_rejected()
    {
        using var factory = new ApiFactory();
        var day = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Segments.AddRange(Row("a", "b", day), Row("a", "b", day));

        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public void ConnectionString_points_into_db_dir() =>
        DbSetup.ConnectionString("./data").Should().Be("Data Source=./data/db/cm.sqlite;Default Timeout=5");
}
