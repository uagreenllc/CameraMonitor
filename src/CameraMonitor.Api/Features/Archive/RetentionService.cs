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

        foreach (var s in plan)
        {
            try { File.Delete(Path.Combine(opt.DataDir, s.Path)); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            archiveBytes -= s.Bytes;
        }
        db.Segments.RemoveRange(plan);
        await db.SaveChangesAsync(ct);
        foreach (var d in plan.Select(s => Path.GetDirectoryName(Path.Combine(opt.DataDir, s.Path))!).Distinct())
            RemoveIfEmpty(d, 2); // hour, then day; never above rec/{cam}/{lens}

        status.OldestSegmentUtc = await db.Segments.MinAsync(s => (DateTime?)s.StartUtc, ct);
        status.ArchiveBytes = Math.Max(0, archiveBytes);
        status.FreeBytes = FreeBytes(opt.DataDir);
        status.LastRunUtc = now;
        status.LastDeleted = plan.Count;
        if (plan.Count > 0) logger.LogInformation("Retention deleted {Count} segments", plan.Count);
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
            catch (IOException) { return; }
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
