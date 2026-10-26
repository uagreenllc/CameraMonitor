using System.Globalization;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Infrastructure;
using CameraMonitor.Api.Infrastructure.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Events;

public static class EventsEndpoints
{
    public static IEndpointRouteBuilder MapEventsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/events", async (string? cam, string? day, int? limit, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (cam is not null && !SafeName.IsSafe(cam)) return Results.Problem("Некоректний ідентифікатор камери.", statusCode: 400, title: "Некоректний запит");
            var d = clock.GetUtcNow().UtcDateTime.Date;
            if (day is not null && !DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                return Results.Problem("Некоректна дата, очікується yyyy-MM-dd.", statusCode: 400, title: "Некоректний запит");
            var end = d.AddDays(1);
            var q = db.Events.Where(e => e.StartedUtc >= d && e.StartedUtc < end);
            if (cam is not null) q = q.Where(e => e.CameraId == cam);
            var rows = await q.OrderByDescending(e => e.StartedUtc).ThenByDescending(e => e.Id).Take(Math.Clamp(limit ?? 200, 1, 1000)).ToListAsync(ct);
            return Results.Ok(rows.Select(e => new
            {
                e.Id,
                Camera = e.CameraId,
                Lens = e.LensId,
                e.Type,
                StartedUtc = Z(e.StartedUtc),
                EndedUtc = e.EndedUtc is { } x ? Z(x) : null,
                Snapshot = e.SnapshotPath is null ? null : $"/events/{e.Id}.jpg",
            }));
        });

        app.MapGet("/events/{id:long}.jpg", async (long id, AppDbContext db, IOptions<ServerOptions> server, HttpContext http, CancellationToken ct) =>
        {
            var rel = await db.Events.Where(e => e.Id == id).Select(e => e.SnapshotPath).FirstOrDefaultAsync(ct);
            if (rel is null) return Results.NotFound();
            var root = Path.GetFullPath(Path.Combine(server.Value.DataDir, "events")) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(server.Value.DataDir, rel));
            if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path)) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(path, "image/jpeg");
        });
        return app;
    }

    internal static string Z(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss.FFF'Z'", CultureInfo.InvariantCulture);
}
