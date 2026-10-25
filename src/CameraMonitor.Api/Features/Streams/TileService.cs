using System.Collections.Concurrent;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Streams;

public sealed class TileService(StreamStateStore store, IOptions<ServerOptions> server, TimeProvider clock, ILogger<TileService> logger)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GrabTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<StreamKey, (DateTimeOffset At, byte[] Jpeg)> _cache = new();
    private readonly SemaphoreSlim _gate = new(4);

    /// <summary>JPEG of the newest finished live segment; null when there is none.</summary>
    public async Task<byte[]?> GetAsync(StreamKey key, CancellationToken ct)
    {
        _cache.TryGetValue(key, out var cached);
        if (cached.Jpeg is not null && clock.GetUtcNow() - cached.At < CacheFor) return cached.Jpeg;

        if (!store.TryGet(key, out var state) || state.LiveDir.Length == 0 || !Directory.Exists(state.LiveDir)) return cached.Jpeg;
        // Segments are named index0.ts, index1.ts ... index10.ts, so name order is wrong: order by write time.
        var names = new DirectoryInfo(state.LiveDir).EnumerateFiles("*.ts")
            .OrderBy(f => f.LastWriteTimeUtc).Select(f => f.Name).ToList();
        var segment = PickLatestFinishedSegment(names);
        if (segment is null) return cached.Jpeg;

        await _gate.WaitAsync(ct);
        try
        {
            // ponytail: ffmpeg grab per tile; Plan B switches to DVRIP snapshot when measured faster
            string[] args = ["-nostdin", "-loglevel", "error", "-i", Path.Combine(state.LiveDir, segment),
                "-frames:v", "1", "-vf", "scale=320:-2", "-f", "image2", "-c:v", "mjpeg", "-q:v", "5", "pipe:1"];
            var (code, jpeg) = await ProcessRunner.RunAsync(server.Value.FfmpegPath, args, GrabTimeout, ct);
            if (code == 0 && jpeg.Length > 0)
            {
                _cache[key] = (clock.GetUtcNow(), jpeg);
                return jpeg;
            }
            logger.LogDebug("Tile grab for {Key} failed with exit code {Code}", key, code);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug("Tile grab for {Key} timed out", key);
        }
        finally { _gate.Release(); }
        return cached.Jpeg;
    }

    /// <summary>Second-newest file: the newest is still being written. Null if fewer than two.</summary>
    public static string? PickLatestFinishedSegment(IReadOnlyList<string> tsFilesOldestFirst) =>
        tsFilesOldestFirst.Count < 2 ? null : tsFilesOldestFirst[^2];
}
