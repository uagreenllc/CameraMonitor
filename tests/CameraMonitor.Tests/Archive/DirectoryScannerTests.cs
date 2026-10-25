using CameraMonitor.Api.Features.Archive;
using CameraMonitor.Api.Features.Streams;
using CameraMonitor.Api.Infrastructure.Db;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace CameraMonitor.Tests.Archive;

public sealed class DirectoryScannerTests
{
    private static readonly StreamKey Key = new("a", "b");

    private static (ApiFactory Factory, DirectoryScanner Scanner, string RecDir) Create()
    {
        var probe = Substitute.For<IDurationProbe>();
        probe.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(TimeSpan.FromSeconds(7));
        var factory = new ApiFactory();
        var f = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton(probe))));
        var recDir = $"{factory.DataDir}/rec/a/b";
        Directory.CreateDirectory($"{recDir}/20261025/14");
        return (factory, f.Services.GetRequiredService<DirectoryScanner>(), recDir);
    }

    [Fact]
    public async Task Orphan_older_than_30s_is_indexed_with_probed_duration()
    {
        var (factory, scanner, recDir) = Create();
        using var _ = factory;
        var file = $"{recDir}/20261025/14/140330.ts";
        File.WriteAllBytes(file, [1, 2, 3]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-2));

        await scanner.ScanAsync(recDir, Key, full: true, CancellationToken.None);

        using var scope = factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Segments.SingleAsync();
        row.DurationMs.Should().Be(7000);
        row.StartUtc.Should().Be(File.GetLastWriteTimeUtc(file).AddSeconds(-7));
        row.Discontinuity.Should().BeTrue();
        row.Bytes.Should().Be(3);
        row.Path.Should().Be("rec/a/b/20261025/14/140330.ts");
    }

    [Fact]
    public async Task Fresh_file_is_skipped()
    {
        var (factory, scanner, recDir) = Create();
        using var _ = factory;
        File.WriteAllBytes($"{recDir}/20261025/14/140330.ts", [1]);

        await scanner.ScanAsync(recDir, Key, full: false, CancellationToken.None);

        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Segments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void RecentHourDirs_returns_two_newest()
    {
        var root = Path.Combine(Path.GetTempPath(), "cm-hours-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dirs = new[] { "12", "13", "14" }.Select(h => Path.Combine(root, "20261025", h)).ToArray();
            for (var i = 0; i < dirs.Length; i++)
            {
                Directory.CreateDirectory(dirs[i]);
                Directory.SetLastWriteTimeUtc(dirs[i], new DateTime(2026, 10, 25, 10 + i, 0, 0, DateTimeKind.Utc));
            }

            DirectoryScanner.RecentHourDirs(root, 2).Should().Equal(dirs[2], dirs[1]);
        }
        finally { Directory.Delete(root, true); }
    }
}
