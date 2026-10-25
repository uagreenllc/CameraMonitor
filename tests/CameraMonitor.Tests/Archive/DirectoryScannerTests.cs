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

    // Factory: owns the data dir and disposal; Services: the one host the test uses (the derived factory with the fake probe).
    private static (ApiFactory Factory, IServiceProvider Services, DirectoryScanner Scanner, string RecDir, StreamStateStore Store) Create()
    {
        var probe = Substitute.For<IDurationProbe>();
        probe.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(TimeSpan.FromSeconds(7));
        var factory = new ApiFactory().WithoutHostedServices();
        var f = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton(probe))));
        var recDir = $"{factory.DataDir}/rec/a/b";
        Directory.CreateDirectory($"{recDir}/20261025/14");
        return (factory, f.Services, f.Services.GetRequiredService<DirectoryScanner>(), recDir, f.Services.GetRequiredService<StreamStateStore>());
    }

    [Fact]
    public async Task Orphan_older_than_30s_is_indexed_with_probed_duration()
    {
        var (factory, services, scanner, recDir, _) = Create();
        using var _ = factory;
        var file = $"{recDir}/20261025/14/140330.ts";
        File.WriteAllBytes(file, [1, 2, 3]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-2));

        await scanner.ScanAsync(recDir, Key, full: true, CancellationToken.None);

        using var scope = services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Segments.SingleAsync();
        row.DurationMs.Should().Be(7000);
        row.StartUtc.Should().Be(File.GetLastWriteTimeUtc(file).AddSeconds(-7));
        row.Discontinuity.Should().BeTrue();
        row.Bytes.Should().Be(3);
        row.Path.Should().Be("rec/a/b/20261025/14/140330.ts");
    }

    [Fact]
    public async Task Stream_gets_full_scan_once_its_RecDir_is_known()
    {
        var (factory, services, scanner, recDir, store) = Create();
        using var _ = factory;
        // three hour dirs; the old one is outside the two most recent
        var old = $"{recDir}/20261025/10";
        Directory.CreateDirectory(old);
        File.WriteAllBytes($"{old}/100000.ts", [1]);
        File.SetLastWriteTimeUtc($"{old}/100000.ts", DateTime.UtcNow.AddHours(-4));
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-4));
        Directory.CreateDirectory($"{recDir}/20261025/12");
        Directory.SetLastWriteTimeUtc($"{recDir}/20261025/12", DateTime.UtcNow.AddHours(-2));
        Directory.SetLastWriteTimeUtc($"{recDir}/20261025/14", DateTime.UtcNow.AddHours(-1));
        var state = store.GetOrAdd(Key);
        var scanned = new HashSet<StreamKey>();

        await scanner.RunPassAsync(scanned, CancellationToken.None);
        scanned.Should().BeEmpty();
        state.RecDir = recDir;
        await scanner.RunPassAsync(scanned, CancellationToken.None);

        scanned.Should().Contain(Key);
        using var scope = services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Segments.Select(s => s.Path).ToListAsync())
            .Should().Equal("rec/a/b/20261025/10/100000.ts");
    }

    [Fact]
    public async Task Fresh_file_is_skipped()
    {
        var (factory, services, scanner, recDir, _) = Create();
        using var _ = factory;
        File.WriteAllBytes($"{recDir}/20261025/14/140330.ts", [1]);

        await scanner.ScanAsync(recDir, Key, full: false, CancellationToken.None);

        using var scope = services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Segments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Zero_length_orphan_is_deleted_not_indexed()
    {
        var (factory, services, scanner, recDir, _) = Create();
        using var _ = factory;
        var file = $"{recDir}/20261025/14/140330.ts";
        File.WriteAllBytes(file, []);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-2));

        await scanner.ScanAsync(recDir, Key, full: true, CancellationToken.None);

        File.Exists(file).Should().BeFalse();
        using var scope = services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Segments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void RecentHourDirs_returns_two_newest_by_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "cm-hours-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dirs = new[] { "20261024/23", "20261025/00", "20261025/01" }.Select(h => Path.Combine(root, h)).ToArray();
            for (var i = 0; i < dirs.Length; i++)
            {
                Directory.CreateDirectory(dirs[i]);
                Directory.SetLastWriteTimeUtc(dirs[i], new DateTime(2026, 10, 25, 10 + i, 0, 0, DateTimeKind.Utc));
            }

            Directory.SetLastWriteTimeUtc(dirs[0], DateTime.UtcNow); // retention touched the oldest hour: must not matter

            DirectoryScanner.RecentHourDirs(root, 2).Select(Path.GetFullPath).Should().Equal(Path.GetFullPath(dirs[2]), Path.GetFullPath(dirs[1]));
        }
        finally { Directory.Delete(root, true); }
    }
}
