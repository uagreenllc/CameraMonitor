using CameraMonitor.Api.Features.Cameras;

namespace CameraMonitor.Api.Features.Streams;

public sealed class StreamWorker(
    StreamSpec spec, FfmpegFactory ffmpeg, StreamStateStore store, ServerOptions server, TimeProvider clock, ILogger logger)
{
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    public async Task RunAsync(CancellationToken ct)
    {
        var state = store.GetOrAdd(spec.Key);
        state.RecDir = spec.RecDir;
        state.LiveDir = spec.LiveDir;
        var attempt = 0;
        var firstVideoWaitStart = clock.GetUtcNow();

        while (!ct.IsCancellationRequested)
        {
            IFfmpegProcess? proc = null;
            try
            {
                state.Generation++;
                Directory.CreateDirectory(spec.RecDir);
                if (Directory.Exists(spec.LiveDir)) Directory.Delete(spec.LiveDir, recursive: true);
                Directory.CreateDirectory(spec.LiveDir);

                proc = ffmpeg(
                    FfmpegArgs.Recording(spec.RtspUrl, spec.RecDir, spec.LiveDir, server.SegmentSeconds, server.HlsSeconds, server.RecordAudio),
                    spec.Key.ToString());
                await proc.StartAsync(ct);
                var startedAt = clock.GetUtcNow();

                while (!proc.Exited.IsCompleted)
                {
                    await Task.Delay(Tick, clock, ct);
                    var now = clock.GetUtcNow();
                    state.Fps = proc.Progress.Fps;
                    state.Bitrate = proc.Progress.Bitrate;
                    state.LastProgressAt = proc.LastProgressAt;
                    if (proc.LastProgressAt is not null && state.Status != StreamStatus.Online)
                    {
                        store.SetStatus(state, StreamStatus.Online, now);
                        attempt = 0;
                    }

                    if (now - (proc.LastProgressAt ?? startedAt) > StallTimeout)
                    {
                        logger.LogWarning("Stream {Stream}: no video for {Seconds}s, restarting ffmpeg", spec.Key, StallTimeout.TotalSeconds);
                        proc.Kill();
                        break;
                    }
                }

                await proc.DisposeAsync();
                proc = null;
                state.Restarts++;
                var after = clock.GetUtcNow();
                if (state.Status != StreamStatus.Offline && after - (state.LastProgressAt ?? firstVideoWaitStart) > OfflineAfter)
                {
                    logger.LogWarning("Stream {Stream}: offline", spec.Key);
                    store.SetStatus(state, StreamStatus.Offline, after);
                }

                await Task.Delay(Backoff.NextDelay(attempt++), clock, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Stream {Stream}: worker error", spec.Key);
                try
                {
                    await Task.Delay(Backoff.NextDelay(attempt++), clock, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                if (proc is not null)
                {
                    try
                    {
                        proc.Kill();
                        await proc.DisposeAsync();
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Stream {Stream}: ffmpeg cleanup failed", spec.Key);
                    }
                }
            }
        }
    }
}
