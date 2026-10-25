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

    private static readonly TimeSpan StaleFor = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<StreamKey, Lazy<Task<byte[]?>>> _inflight = new();

    internal int GrabCount; // ffmpeg grabs actually started (tests)

    /// <summary>JPEG of the newest finished live segment; null when there is none. A stale tile (max 30 s) is returned only when a fresh grab fails.</summary>
    public async Task<byte[]?> GetAsync(StreamKey key, CancellationToken ct)
    {
        if (_cache.TryGetValue(key, out var cached) && clock.GetUtcNow() - cached.At < CacheFor) return cached.Jpeg;

        if (!store.TryGet(key, out var state) || state.LiveDir.Length == 0 || !Directory.Exists(state.LiveDir)) return null;
        // Segments are named index0.ts, index1.ts ... index10.ts, so name order is wrong: order by write time.
        var names = new DirectoryInfo(state.LiveDir).EnumerateFiles("*.ts")
            .OrderBy(f => f.LastWriteTimeUtc).Select(f => f.Name).ToList();
        var segment = PickLatestFinishedSegment(names);
        if (segment is null) return null;

        // One grab per key at a time; concurrent viewers share it (it ignores the caller's token, the 5 s timeout bounds it).
        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<byte[]?>>(() => Task.Run(() => GrabAsync(key, Path.Combine(state.LiveDir, segment)))));
        return await lazy.Value.WaitAsync(ct);
    }

    private async Task<byte[]?> GrabAsync(StreamKey key, string segmentPath)
    {
        try
        {
            await _gate.WaitAsync();
            try
            {
                if (_cache.TryGetValue(key, out var fresh) && clock.GetUtcNow() - fresh.At < CacheFor) return fresh.Jpeg;
                Interlocked.Increment(ref GrabCount);
                // ponytail: ffmpeg grab per tile; Plan B switches to DVRIP snapshot when measured faster
                string[] args = ["-nostdin", "-loglevel", "error", "-i", segmentPath,
                    "-frames:v", "1", "-vf", "scale=320:-2", "-f", "image2", "-c:v", "mjpeg", "-q:v", "5", "pipe:1"];
                var (code, jpeg) = await ProcessRunner.RunAsync(server.Value.FfmpegPath, args, GrabTimeout, CancellationToken.None);
                if (code == 0 && jpeg.Length > 0)
                {
                    _cache[key] = (clock.GetUtcNow(), jpeg);
                    return jpeg;
                }
                logger.LogDebug("Tile grab for {Key} failed with exit code {Code}", key, code);
            }
            catch (Exception e) when (e is OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                logger.LogDebug("Tile grab for {Key} failed: {Reason}", key, e.GetType().Name);
            }
            finally { _gate.Release(); }

            return _cache.TryGetValue(key, out var stale) && clock.GetUtcNow() - stale.At < StaleFor ? stale.Jpeg : null;
        }
        finally { _inflight.TryRemove(key, out _); }
    }

    /// <summary>Second-newest file: the newest is still being written. Null if fewer than two.</summary>
    public static string? PickLatestFinishedSegment(IReadOnlyList<string> tsFilesOldestFirst) =>
        tsFilesOldestFirst.Count < 2 ? null : tsFilesOldestFirst[^2];
}
