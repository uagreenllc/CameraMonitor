using System.Globalization;
using System.Text.RegularExpressions;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Infrastructure;
using CameraMonitor.Api.Infrastructure.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Archive;

public static partial class ArchiveEndpoints
{
    [GeneratedRegex(@"\A\d{8}\z")] private static partial Regex DateRx();
    [GeneratedRegex(@"\A\d{2}\z")] private static partial Regex HourRx();
    [GeneratedRegex(@"\A\d{6}\.ts\z")] private static partial Regex FileRx();

    private static IResult Bad(string detail) => Results.Problem(detail, statusCode: 400, title: "Некоректний запит");

    /// <summary>Validates ids and the from/to window; returns an error result (or null) and the parsed UTC range.</summary>
    private static IResult? ParseRange(string cam, string lens, string? from, string? to, TimeSpan max, out DateTime fromUtc, out DateTime toUtc)
    {
        fromUtc = toUtc = default;
        if (!SafeName.IsSafe(cam) || !SafeName.IsSafe(lens)) return Bad("Некоректний ідентифікатор камери.");
        if (!TryIso(from, out fromUtc) || !TryIso(to, out toUtc)) return Bad("Потрібні параметри from і to у форматі ISO-8601.");
        if (fromUtc >= toUtc) return Bad("Початок має бути раніше за кінець.");
        if (toUtc - fromUtc > max) return Bad($"Діапазон завеликий: максимум {max.TotalHours:0} год.");
        return null;
    }

    private static bool TryIso(string? s, out DateTime utc)
    {
        utc = default;
        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var o)) return false;
        utc = o.UtcDateTime;
        return true;
    }

    private static async Task<List<SegmentRow>> OverlappingAsync(AppDbContext db, string cam, string lens, DateTime from, DateTime to, int segmentSeconds, CancellationToken ct)
    {
        var rows = await db.Segments
            .Where(s => s.CameraId == cam && s.LensId == lens && s.StartUtc < to && s.StartUtc >= from.AddSeconds(-2 * segmentSeconds))
            .OrderBy(s => s.StartUtc).ToListAsync(ct);
        return rows.Where(s => s.StartUtc.AddMilliseconds(s.DurationMs) > from).ToList();
    }

    private static string Z(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss.FFF'Z'", CultureInfo.InvariantCulture);

    public static IEndpointRouteBuilder MapArchiveEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/archive/{cam}/{lens}.m3u8", async (string cam, string lens, string? from, string? to, AppDbContext db,
            IOptions<ServerOptions> server, HttpContext http, CancellationToken ct) =>
        {
            if (ParseRange(cam, lens, from, to, TimeSpan.FromHours(24), out var f, out var t) is { } bad) return bad;
            var rows = await OverlappingAsync(db, cam, lens, f, t, server.Value.SegmentSeconds, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Text(PlaylistBuilder.BuildVod(rows, r => "/seg/" + r.Path["rec/".Length..]), "application/vnd.apple.mpegurl");
        });

        app.MapGet("/seg/{cam}/{lens}/{date}/{hour}/{file}", (string cam, string lens, string date, string hour, string file,
            IOptions<ServerOptions> server, HttpContext http) =>
        {
            if (!SafeName.IsSafe(cam) || !SafeName.IsSafe(lens) || !DateRx().IsMatch(date) || !HourRx().IsMatch(hour) || !FileRx().IsMatch(file))
                return Results.BadRequest();
            var root = Path.GetFullPath(Path.Combine(server.Value.DataDir, "rec")) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, cam, lens, date, hour, file));
            if (!path.StartsWith(root, StringComparison.Ordinal)) return Results.BadRequest();
            if (!File.Exists(path)) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(path, "video/mp2t", enableRangeProcessing: false);
        });

        app.MapGet("/api/timeline", async (string cam, string lens, string day, AppDbContext db, CancellationToken ct) =>
        {
            if (!SafeName.IsSafe(cam) || !SafeName.IsSafe(lens)) return Bad("Некоректний ідентифікатор камери.");
            if (!DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
                return Bad("Некоректна дата, очікується yyyy-MM-dd.");
            var end = d.AddDays(1);
            var rows = await db.Segments.Where(s => s.CameraId == cam && s.LensId == lens && s.StartUtc >= d && s.StartUtc < end)
                .OrderBy(s => s.StartUtc).ToListAsync(ct);
            var ranges = TimelineBuilder.Merge(rows, TimeSpan.FromSeconds(1.5)).Select(r => new { from = Z(r.FromUtc), to = Z(r.ToUtc) });
            var events = (await db.Events.Where(e => e.CameraId == cam && e.LensId == lens && e.StartedUtc >= d && e.StartedUtc < end)
                .OrderBy(e => e.StartedUtc).ToListAsync(ct))
                .Select(e => new { e.Type, Lens = e.LensId, from = Z(e.StartedUtc), to = e.EndedUtc is { } x ? Z(x) : null });
            return Results.Ok(new { ranges, events });
        });

        app.MapGet("/api/export", async (string cam, string lens, string? from, string? to, AppDbContext db, ExportService export,
            IOptions<ServerOptions> server, ILogger<ExportService> log, HttpContext http, CancellationToken ct) =>
        {
            if (ParseRange(cam, lens, from, to, ExportService.MaxRange, out var f, out var t) is { } bad) return bad;
            var rows = await OverlappingAsync(db, cam, lens, f, t, server.Value.SegmentSeconds, ct);
            var root = Path.GetFullPath(Path.Combine(server.Value.DataDir, "rec")) + Path.DirectorySeparatorChar;
            var paths = new List<string>();
            foreach (var r in rows)
            {
                var full = Path.GetFullPath(Path.Combine(server.Value.DataDir, r.Path));
                if (!full.StartsWith(root, StringComparison.Ordinal))
                {
                    log.LogWarning("Export skipped segment {Id} outside the rec root", r.Id);
                    continue;
                }
                // a segment deleted by retention mid-range is skipped: concat then has a gap by design
                if (!File.Exists(full)) { log.LogDebug("Export skipped missing segment {Path}", r.Path); continue; }
                paths.Add(full);
            }
            if (paths.Count == 0) return Results.NotFound();
            http.Response.ContentType = "video/mp4";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"{cam}-{lens}-{f:yyyyMMdd-HHmm}.mp4\"";
            try
            {
                var code = await export.WriteMp4Async(paths, http.Response.Body, ct);
                if (code != 0)
                {
                    log.LogWarning("Export {Cam}/{Lens} {From:o}..{To:o} failed with ffmpeg exit code {Code}; aborting response", cam, lens, f, t, code);
                    http.Abort();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Export {Cam}/{Lens} {From:o}..{To:o} failed; aborting response", cam, lens, f, t);
                http.Abort();
            }
            return Results.Empty;
        });
        return app;
    }
}
