using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Streams;
using CameraMonitor.Api.Infrastructure.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Archive;

public sealed class DirectoryScanner(
    IServiceScopeFactory scopes, StreamStateStore store, IOptions<ServerOptions> server, IDurationProbe probe, TimeProvider clock, ILogger<DirectoryScanner> logger) : BackgroundService
{
    public static readonly TimeSpan MinAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>Hour directories (recDir/yyyyMMdd/HH) ordered by last write time, newest first.</summary>
    public static IReadOnlyList<string> RecentHourDirs(string recDir, int count) =>
        HourDirs(recDir).OrderByDescending(Directory.GetLastWriteTimeUtc).Take(count).ToList();

    private static IEnumerable<string> HourDirs(string recDir) =>
        Directory.Exists(recDir) ? Directory.EnumerateDirectories(recDir).SelectMany(Directory.EnumerateDirectories) : [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            for (var waited = 0; store.All().Count == 0 && waited < 10; waited++) await Task.Delay(TimeSpan.FromSeconds(1), clock, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }

        var full = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var state in store.All())
                    if (state.RecDir.Length > 0) await ScanAsync(state.RecDir, state.Key, full, ct);
                full = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Directory scan failed");
            }

            try { await Task.Delay(Interval, clock, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    public async Task ScanAsync(string recDir, StreamKey key, bool full, CancellationToken ct)
    {
        var dirs = full ? HourDirs(recDir).ToList() : RecentHourDirs(recDir, 2);
        var dataDir = server.Value.DataDir;
        var cutoff = clock.GetUtcNow().UtcDateTime - MinAge;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var dir in dirs)
        {
            var prefix = SegmentIndexer.RelativePath(dataDir, dir) + "/";
            var known = (await db.Segments.Where(s => s.Path.StartsWith(prefix)).Select(s => s.Path).ToListAsync(ct)).ToHashSet();
            foreach (var abs in Directory.EnumerateFiles(dir, "*.ts"))
            {
                var rel = SegmentIndexer.RelativePath(dataDir, abs);
                var written = File.GetLastWriteTimeUtc(abs);
                if (written >= cutoff || known.Contains(rel)) continue;

                var duration = await probe.ProbeAsync(abs, ct) ?? TimeSpan.FromSeconds(server.Value.SegmentSeconds);
                db.Segments.Add(new SegmentRow
                {
                    CameraId = key.CameraId,
                    LensId = key.LensId,
                    StartUtc = DateTime.SpecifyKind(written - duration, DateTimeKind.Utc),
                    DurationMs = (int)Math.Round(duration.TotalMilliseconds),
                    Path = rel,
                    Bytes = new FileInfo(abs).Length,
                    Discontinuity = true,
                });
                try
                {
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("Indexed orphan segment {Path}", rel);
                }
                catch (DbUpdateException ex)
                {
                    logger.LogDebug(ex, "Segment {Path} already indexed", rel);
                    db.ChangeTracker.Clear();
                }
            }
        }
    }
}
