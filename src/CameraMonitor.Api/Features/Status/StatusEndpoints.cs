using CameraMonitor.Api.Features.Streams;

namespace CameraMonitor.Api.Features.Status;

public static class StatusEndpoints
{
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/status", (StreamStateStore store) => new
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
        });
        return app;
    }
}
