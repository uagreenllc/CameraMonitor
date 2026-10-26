using CameraMonitor.Api.Features.Archive;
using CameraMonitor.Api.Features.Backfill;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Events;
using CameraMonitor.Api.Features.Streams;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Status;

public static class StatusEndpoints
{
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/status", (StreamStateStore store, XmAgentStore agents, RetentionStatus disk, BackfillStatus backfill, IOptions<ServerOptions> server, TimeProvider clock) => new
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
            Cameras = agents.All().OrderBy(a => a.CameraId).Select(a => new
            {
                Camera = a.CameraId,
                a.Connected,
                a.ConnectedSince,
                a.LastError,
                a.LastAlarmAt,
                a.ClockDriftSeconds,
                a.Reconnects,
                a.Gop,
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
            Backfill = backfill.All().Select(b => new
            {
                Camera = b.CameraId,
                b.Queued,
                b.Current,
                b.FilesDone,
                b.SegmentsAdded,
                b.GapsNoFiles,
                b.LastError,
                b.LastRunUtc,
            }),
        });
        return app;
    }

    private static double Gb(long bytes) => Math.Round(bytes / 1073741824.0, 1);
}
