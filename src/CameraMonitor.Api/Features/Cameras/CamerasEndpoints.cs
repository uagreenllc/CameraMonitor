using CameraMonitor.Api.Features.Streams;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Cameras;

public static class CamerasEndpoints
{
    public static IEndpointRouteBuilder MapCamerasEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cameras", (IOptionsMonitor<CamerasOptions> cameras, StreamStateStore store) =>
            cameras.CurrentValue.Items.Select(c => new
            {
                c.Id,
                c.Name,
                Lenses = c.Lenses.Select(l =>
                {
                    var found = store.TryGet(new StreamKey(c.Id, l.Id), out var s);
                    return new
                    {
                        l.Id,
                        l.Ptz,
                        Status = found ? s.Status : StreamStatus.Starting,
                        LastProgressAt = found ? s.LastProgressAt : null,
                        Restarts = found ? s.Restarts : 0,
                        Fps = found ? s.Fps : 0,
                        Bitrate = found ? s.Bitrate : null,
                    };
                }),
            }));
        return app;
    }
}
