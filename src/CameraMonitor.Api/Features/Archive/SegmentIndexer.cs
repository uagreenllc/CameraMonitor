using System.Collections.Concurrent;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Streams;
using CameraMonitor.Api.Infrastructure.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Archive;

public sealed class SegmentIndexer(
    IServiceScopeFactory scopes, StreamStateStore store, IOptions<ServerOptions> server, TimeProvider clock, ILogger<SegmentIndexer> logger) : BackgroundService
{
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<StreamKey, DateTime?> _prevEnd = new();

    public static bool IsDiscontinuity(DateTime? previousEndUtc, DateTime startUtc) =>
        previousEndUtc is not { } prev || Math.Abs((startUtc - prev).TotalMilliseconds) > 1500;

    public static string RelativePath(string dataDir, string absolutePath) =>
        Path.GetRelativePath(Path.GetFullPath(dataDir), Path.GetFullPath(absolutePath)).Replace('\\', '/');

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await IndexOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Segment indexing failed");
            }

            try { await Task.Delay(Poll, clock, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    public async Task IndexOnceAsync(CancellationToken ct)
    {
        foreach (var state in store.All())
        {
            if (state.RecDir.Length == 0) continue;
            try { await IndexStreamAsync(state.Key, state.RecDir, ct); }
            catch (IOException ex) { logger.LogDebug(ex, "Playlist of {Key} busy, retry next poll", state.Key); }
        }
    }

    private async Task IndexStreamAsync(StreamKey key, string recDir, CancellationToken ct)
    {
        var playlist = Path.Combine(recDir, "archive.m3u8");
        if (!File.Exists(playlist)) return;
        var entries = HlsPlaylistParser.Parse(await File.ReadAllTextAsync(playlist, ct));
        if (entries.Count == 0) return;

        var dataDir = server.Value.DataDir;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!_prevEnd.TryGetValue(key, out var prevEnd))
        {
            var last = await db.Segments.Where(s => s.CameraId == key.CameraId && s.LensId == key.LensId)
                .OrderByDescending(s => s.StartUtc).Select(s => new { s.StartUtc, s.DurationMs }).FirstOrDefaultAsync(ct);
            prevEnd = last is null ? null : last.StartUtc.AddMilliseconds(last.DurationMs);
        }

        var files = new List<(PlaylistEntry Entry, string Abs, string Rel)>();
        foreach (var entry in entries)
        {
            // ffmpeg writes URIs as given on its command line (relative to the process cwd, e.g. ./data/rec/...) or relative to the playlist.
            var abs = Path.IsPathRooted(entry.FileName) ? entry.FileName : Path.Combine(recDir, entry.FileName);
            if (!File.Exists(abs)) abs = Path.GetFullPath(entry.FileName);
            if (File.Exists(abs)) files.Add((entry, abs, RelativePath(dataDir, abs)));
        }
        var rels = files.Select(f => f.Rel).ToList();
        var known = (await db.Segments.Where(s => rels.Contains(s.Path)).Select(s => s.Path).ToListAsync(ct)).ToHashSet();

        foreach (var (entry, abs, rel) in files)
        {
            if (known.Contains(rel)) continue;
            var start = entry.ProgramDateTime.UtcDateTime;
            var durationMs = (int)Math.Round(entry.DurationSeconds * 1000);
            db.Segments.Add(new SegmentRow
            {
                CameraId = key.CameraId,
                LensId = key.LensId,
                StartUtc = start,
                DurationMs = durationMs,
                Path = rel,
                Bytes = new FileInfo(abs).Length,
                Discontinuity = IsDiscontinuity(prevEnd, start),
            });
            try
            {
                await db.SaveChangesAsync(ct);
                prevEnd = start.AddMilliseconds(durationMs);
            }
            catch (DbUpdateException ex)
            {
                logger.LogDebug(ex, "Segment {Path} already indexed", rel);
                db.ChangeTracker.Clear();
            }
        }
        _prevEnd[key] = prevEnd;
    }
}
