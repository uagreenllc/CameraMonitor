using CameraMonitor.Api.Features.Archive;
using CameraMonitor.Api.Infrastructure.Db;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CameraMonitor.Tests.Archive;

public sealed class RetentionServiceTests
{
    [Fact]
    public async Task RunOnceAsync_deletes_expired_files_rows_and_empty_dirs()
    {
        using var factory = new ApiFactory().WithoutHostedServices().WithSetting("Server:RetentionDays", "14");
        factory.CreateClient(); // start host
        var now = DateTime.UtcNow;
        var lens = $"{factory.DataDir}/rec/a/b";
        var ages = new[] { 20, 15, 1 };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var d in ages)
            {
                var t = now.AddDays(-d);
                var rel = $"rec/a/b/{t:yyyyMMdd}/{t:HH}/{t:HHmmss}.ts";
                Directory.CreateDirectory(Path.GetDirectoryName($"{factory.DataDir}/{rel}")!);
                File.WriteAllBytes($"{factory.DataDir}/{rel}", new byte[1024]);
                db.Segments.Add(new SegmentRow { CameraId = "a", LensId = "b", StartUtc = t, DurationMs = 10000, Path = rel, Bytes = 1024 });
            }
            await db.SaveChangesAsync();
        }

        await factory.Services.GetRequiredService<RetentionService>().RunOnceAsync(CancellationToken.None);

        using var scope2 = factory.Services.CreateScope();
        (await scope2.ServiceProvider.GetRequiredService<AppDbContext>().Segments.CountAsync()).Should().Be(1);
        foreach (var d in new[] { 20, 15 })
            Directory.Exists($"{lens}/{now.AddDays(-d):yyyyMMdd}").Should().BeFalse();
        Directory.Exists($"{lens}/{now.AddDays(-1):yyyyMMdd}").Should().BeTrue();
        var status = factory.Services.GetRequiredService<RetentionStatus>();
        status.LastDeleted.Should().Be(2);
        status.ArchiveBytes.Should().Be(1024);
    }

    [Fact]
    public async Task RunOnceAsync_skips_an_undeletable_file_and_deletes_the_rest()
    {
        if (!OperatingSystem.IsWindows()) return; // FileShare.None blocks deletion only on Windows
        using var factory = new ApiFactory().WithoutHostedServices().WithSetting("Server:RetentionDays", "14");
        factory.CreateClient();
        var now = DateTime.UtcNow;
        var rels = new List<string>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var d in new[] { 20, 15 })
            {
                var t = now.AddDays(-d);
                var rel = $"rec/a/b/{t:yyyyMMdd}/{t:HH}/{t:HHmmss}.ts";
                rels.Add(rel);
                Directory.CreateDirectory(Path.GetDirectoryName($"{factory.DataDir}/{rel}")!);
                File.WriteAllBytes($"{factory.DataDir}/{rel}", new byte[1024]);
                db.Segments.Add(new SegmentRow { CameraId = "a", LensId = "b", StartUtc = t, DurationMs = 10000, Path = rel, Bytes = 1024 });
            }
            await db.SaveChangesAsync();
        }

        using (new FileStream($"{factory.DataDir}/{rels[0]}", FileMode.Open, FileAccess.Read, FileShare.None))
            await factory.Services.GetRequiredService<RetentionService>().RunOnceAsync(CancellationToken.None);

        using var scope2 = factory.Services.CreateScope();
        (await scope2.ServiceProvider.GetRequiredService<AppDbContext>().Segments.Select(s => s.Path).ToListAsync())
            .Should().Equal(rels[0]);
        File.Exists($"{factory.DataDir}/{rels[0]}").Should().BeTrue();
        File.Exists($"{factory.DataDir}/{rels[1]}").Should().BeFalse();
        factory.Services.GetRequiredService<RetentionStatus>().LastDeleted.Should().Be(1);
    }
}
