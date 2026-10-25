using CameraMonitor.Api.Features.Archive;
using CameraMonitor.Api.Features.Streams;
using CameraMonitor.Api.Infrastructure.Db;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CameraMonitor.Tests.Archive;

public sealed class SegmentIndexerTests
{
    private static readonly DateTime T = new(2026, 10, 25, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsDiscontinuity_cases()
    {
        SegmentIndexer.IsDiscontinuity(null, T).Should().BeTrue();
        SegmentIndexer.IsDiscontinuity(T, T).Should().BeFalse();
        SegmentIndexer.IsDiscontinuity(T, T.AddSeconds(1.4)).Should().BeFalse();
        SegmentIndexer.IsDiscontinuity(T, T.AddSeconds(2)).Should().BeTrue();
        SegmentIndexer.IsDiscontinuity(T, T.AddSeconds(-2)).Should().BeTrue();
    }

    [Fact]
    public void RelativePath_works_with_relative_dirs() =>
        SegmentIndexer.RelativePath("./data", "./data/rec/a/b/20261025/14/140320.ts").Should().Be("rec/a/b/20261025/14/140320.ts");

    [Fact]
    public async Task IndexOnceAsync_inserts_new_entries_once()
    {
        using var factory = new ApiFactory();
        var recDir = $"{factory.DataDir}/rec/a/b";
        Directory.CreateDirectory($"{recDir}/20261025/14");
        File.WriteAllText($"{recDir}/archive.m3u8", HlsPlaylistParserTests.Fixture);
        File.WriteAllBytes($"{recDir}/20261025/14/140310.ts", [1]);
        File.WriteAllBytes($"{recDir}/20261025/14/140320.ts", [1]);
        factory.Services.GetRequiredService<StreamStateStore>().GetOrAdd(new StreamKey("a", "b")).RecDir = recDir;
        var indexer = factory.Services.GetRequiredService<SegmentIndexer>();

        await indexer.IndexOnceAsync(CancellationToken.None);
        await indexer.IndexOnceAsync(CancellationToken.None);

        using var scope = factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Segments.OrderBy(s => s.StartUtc).ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].Discontinuity.Should().BeTrue();
        rows[1].Discontinuity.Should().BeFalse();
        rows[1].Path.Should().Be("rec/a/b/20261025/14/140320.ts");
        rows[1].DurationMs.Should().Be(9960);
        rows[1].Bytes.Should().Be(1);
        rows[1].StartUtc.Kind.Should().Be(DateTimeKind.Utc);
    }
}
