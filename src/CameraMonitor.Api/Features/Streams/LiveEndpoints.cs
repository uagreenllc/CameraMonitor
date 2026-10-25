using CameraMonitor.Api.Infrastructure;

namespace CameraMonitor.Api.Features.Streams;

public static class LiveEndpoints
{
    public static IEndpointRouteBuilder MapLiveEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/live/{cam}/{lens}/{file}", (string cam, string lens, string file, StreamStateStore store, HttpContext http) =>
        {
            if (!SafeName.IsSafe(cam) || !SafeName.IsSafe(lens) || !SafeName.IsSafe(file)) return Results.BadRequest();
            if (!store.TryGet(new StreamKey(cam, lens), out var state) || state.LiveDir.Length == 0) return Results.NotFound();
            var path = Path.GetFullPath(Path.Combine(state.LiveDir, file));
            if (!File.Exists(path)) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            var type = file.EndsWith(".m3u8", StringComparison.Ordinal) ? "application/vnd.apple.mpegurl"
                : file.EndsWith(".ts", StringComparison.Ordinal) ? "video/mp2t" : "application/octet-stream";
            return Results.File(path, type, enableRangeProcessing: false);
        });

        app.MapGet("/api/tile/{cam}/{lens}.jpg", async (string cam, string lens, TileService tiles, HttpContext http, CancellationToken ct) =>
        {
            if (!SafeName.IsSafe(cam) || !SafeName.IsSafe(lens)) return Results.BadRequest();
            var jpeg = await tiles.GetAsync(new StreamKey(cam, lens), ct);
            if (jpeg is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(jpeg, "image/jpeg");
        });
        return app;
    }
}
