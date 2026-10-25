using CameraMonitor.Api.Features.Archive;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Streams;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Status;

public static class StatusEndpoints
{
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/status", (StreamStateStore store, RetentionStatus disk, IOptions<ServerOptions> server, TimeProvider clock) => new
        {
            Streams = store.All().OrderBy(s => s.Key.CameraId).ThenBy(s => s.Key.LensId).Select(s => new
            {
                Camera = s.Key.CameraId,
                Lens = s.Key.LensId,
                s.Status,
                s.LastProgressAt,
                s.Restarts,
                s.Generation,
                s.Fps,
                s.Bitrate,
            }),
            Disk = new
            {
                FreeGb = Gb(disk.FreeBytes),
                ArchiveGb = Gb(disk.ArchiveBytes),
                disk.OldestSegmentUtc,
                EffectiveRetentionDays = disk.OldestSegmentUtc is { } o ? (double?)Math.Round((clock.GetUtcNow().UtcDateTime - o).TotalDays, 1) : null,
                server.Value.MinRetentionDays,
                disk.LastRunUtc,
                disk.LastDeleted,
            },
        });
        return app;
    }

    private static double Gb(long bytes) => Math.Round(bytes / 1073741824.0, 1);
}
