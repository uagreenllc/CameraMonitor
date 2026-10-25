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

    /// <summary>Hour directories (recDir/yyyyMMdd/HH) ordered by name (= time), newest first; mtime is unreliable (retention touches old dirs).</summary>
    public static IReadOnlyList<string> RecentHourDirs(string recDir, int count) =>
        HourDirs(recDir).OrderByDescending(d => d, StringComparer.Ordinal).Take(count).ToList();

    private static IEnumerable<string> HourDirs(string recDir) =>
        Directory.Exists(recDir) ? Directory.EnumerateDirectories(recDir).SelectMany(Directory.EnumerateDirectories) : [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            for (var waited = 0; !store.All().Any(s => s.RecDir.Length > 0) && waited < 10; waited++)
                await Task.Delay(TimeSpan.FromSeconds(1), clock, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }

        var fullyScanned = new HashSet<StreamKey>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(fullyScanned, ct);
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

    /// <summary>One pass over all streams; a stream gets a full scan the first time its RecDir is known.</summary>
    internal async Task RunPassAsync(HashSet<StreamKey> fullyScanned, CancellationToken ct)
    {
        var states = store.All();
        fullyScanned.IntersectWith(states.Select(s => s.Key));
        foreach (var state in states)
        {
            if (state.RecDir.Length == 0) continue;
            await ScanAsync(state.RecDir, state.Key, full: !fullyScanned.Contains(state.Key), ct);
            fullyScanned.Add(state.Key);
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
            // camera+lens hit the (CameraId, LensId, StartUtc) index; StartsWith alone is a full scan on SQLite
            var known = (await db.Segments
                .Where(s => s.CameraId == key.CameraId && s.LensId == key.LensId && s.Path.StartsWith(prefix))
                .Select(s => s.Path).ToListAsync(ct)).ToHashSet();
            foreach (var abs in Directory.EnumerateFiles(dir, "*.ts"))
            {
                var rel = SegmentIndexer.RelativePath(dataDir, abs);
                if (known.Contains(rel)) continue; // before any stat: a full scan touches every indexed file
                var info = new FileInfo(abs);
                var written = info.LastWriteTimeUtc;
                if (written >= cutoff) continue;
                if (info.Length == 0)
                {
                    // hls.js chokes on an empty TS in a VOD playlist
                    try { info.Delete(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    logger.LogDebug("Deleted empty orphan segment {Path}", rel);
                    continue;
                }

                var duration = await probe.ProbeAsync(abs, ct) ?? TimeSpan.FromSeconds(server.Value.SegmentSeconds);
                db.Segments.Add(new SegmentRow
                {
                    CameraId = key.CameraId,
                    LensId = key.LensId,
                    StartUtc = DateTime.SpecifyKind(written - duration, DateTimeKind.Utc),
                    DurationMs = (int)Math.Round(duration.TotalMilliseconds),
                    Path = rel,
                    Bytes = info.Length,
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
