using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Infrastructure.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Archive;

public sealed class RetentionService(IServiceScopeFactory scopes, IOptions<ServerOptions> server, RetentionStatus status, TimeProvider clock, ILogger<RetentionService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private const int BatchSize = 5000;
    private bool _warnedFree;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Retention run failed"); }
            try { await Task.Delay(Interval, clock, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var opt = server.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var oldest = await db.Segments.OrderBy(s => s.StartUtc).Take(BatchSize).ToListAsync(ct);
        var archiveBytes = await db.Segments.SumAsync(s => (long?)s.Bytes, ct) ?? 0;
        var plan = RetentionPlanner.Plan(new RetentionInput(oldest, now, TimeSpan.FromDays(opt.RetentionDays), FreeBytes(opt.DataDir), opt.MinFreeGb * 1024L * 1024 * 1024));

        var deleted = new List<SegmentRow>(plan.Count);
        var failed = new List<string>();
        foreach (var s in plan)
        {
            try { File.Delete(Path.Combine(opt.DataDir, s.Path)); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(s.Path); // keep the row: the operator sees it again on the next run
                continue;
            }

            deleted.Add(s);
            archiveBytes -= s.Bytes;
        }

        if (failed.Count > 0) logger.LogWarning("Retention could not delete {Count} segment files, e.g. {Path}", failed.Count, failed[0]);
        db.Segments.RemoveRange(deleted);
        await db.SaveChangesAsync(ct);
        foreach (var d in deleted.Select(s => Path.GetDirectoryName(Path.Combine(opt.DataDir, s.Path))!).Distinct())
            RemoveIfEmpty(d, 2); // hour, then day; never above rec/{cam}/{lens}

        await DeleteOldEventsAsync(db, opt, now, ct);

        if (deleted.Count > 0)
        {
            var evicted = deleted.Max(s => s.StartUtc.AddMilliseconds(s.DurationMs));
            status.EvictedThroughUtc = status.EvictedThroughUtc > evicted ? status.EvictedThroughUtc : evicted;
        }

        status.OldestSegmentUtc = await db.Segments.MinAsync(s => (DateTime?)s.StartUtc, ct);
        status.ArchiveBytes = Math.Max(0, archiveBytes);
        status.FreeBytes = FreeBytes(opt.DataDir);
        status.LastRunUtc = now;
        status.LastDeleted = deleted.Count;
        if (deleted.Count > 0) logger.LogInformation("Retention deleted {Count} segments", deleted.Count);
    }

    private async Task DeleteOldEventsAsync(AppDbContext db, ServerOptions opt, DateTime now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-opt.RetentionDays);
        var old = await db.Events.Where(e => e.StartedUtc < cutoff).ToListAsync(ct);
        if (old.Count == 0) return;
        foreach (var e in old.Where(e => e.SnapshotPath is not null))
        {
            try { File.Delete(Path.Combine(opt.DataDir, e.SnapshotPath!)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Cannot delete event snapshot {Path}", e.SnapshotPath); }
        }

        db.Events.RemoveRange(old);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Retention deleted {Count} events", old.Count);
    }

    private static void RemoveIfEmpty(string dir, int levels)
    {
        for (var i = 0; i < levels; i++)
        {
            try
            {
                if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) return;
                Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            dir = Path.GetDirectoryName(dir)!;
        }
    }

    private long FreeBytes(string dataDir)
    {
        try { return new DriveInfo(Path.GetFullPath(dataDir)).AvailableFreeSpace; }
        catch (Exception ex)
        {
            if (!_warnedFree) { _warnedFree = true; logger.LogWarning(ex, "Cannot read free space of {Dir}; free-space retention disabled", dataDir); }
            return long.MaxValue;
        }
    }
}
