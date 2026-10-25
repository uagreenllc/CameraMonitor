using CameraMonitor.Api.Features.Cameras;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Streams;

public sealed class StreamSupervisor(
    IOptionsMonitor<CamerasOptions> cameras, IOptions<ServerOptions> server, StreamStateStore store,
    FfmpegFactory ffmpeg, TimeProvider clock, ILoggerFactory loggers) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private readonly Dictionary<StreamSpec, (CancellationTokenSource Cts, Task Run)> _workers = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _log = loggers.CreateLogger<StreamSupervisor>();
    private CancellationTokenSource? _pending;

    public static IReadOnlyList<StreamSpec> BuildSpecs(CamerasOptions cameras, ServerOptions server)
    {
        var specs = new List<StreamSpec>();
        foreach (var cam in cameras.Items)
        {
            var password = CameraPasswords.Resolve(cameras, cam) ?? "";
            foreach (var lens in cam.Lenses)
            {
                specs.Add(new StreamSpec(
                    new StreamKey(cam.Id, lens.Id),
                    RtspUrlBuilder.Build(cam, lens, password),
                    FfmpegArgs.NormalizePath($"{server.DataDir}/rec/{cam.Id}/{lens.Id}"),
                    FfmpegArgs.NormalizePath($"{server.LiveDir}/{cam.Id}/{lens.Id}")));
            }
        }

        return specs;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ApplyAsync(stoppingToken);
            using var sub = cameras.OnChange(_ => ScheduleReload(stoppingToken));
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stream supervisor failed to start streams");
        }

        _pending?.Cancel();
        await _gate.WaitAsync();
        try
        {
            await Task.WhenAll(_workers.Values.Select(Stop));
            _workers.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    // File watchers fire twice per save: restart the debounce timer on every change.
    private void ScheduleReload(CancellationToken stopping)
    {
        var next = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        Interlocked.Exchange(ref _pending, next)?.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Debounce, clock, next.Token);
                await ApplyAsync(next.Token);
            }
            catch (OperationCanceledException)
            {
                // superseded or stopping
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Camera config reload failed");
            }
        });
    }

    private async Task ApplyAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wanted = BuildSpecs(cameras.CurrentValue, server.Value);
            foreach (var spec in _workers.Keys.Except(wanted).ToList())
            {
                _log.LogInformation("Stopping stream {Stream}", spec.Key);
                await Stop(_workers[spec]);
                _workers.Remove(spec);
                store.Remove(spec.Key);
            }

            foreach (var spec in wanted.Where(s => !_workers.ContainsKey(s)))
            {
                _log.LogInformation("Starting stream {Stream}", spec.Key);
                var worker = new StreamWorker(spec, ffmpeg, store, server.Value, clock, loggers.CreateLogger<StreamWorker>());
                var cts = new CancellationTokenSource();
                _workers[spec] = (cts, Task.Run(() => worker.RunAsync(cts.Token)));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task Stop((CancellationTokenSource Cts, Task Run) w)
    {
        await w.Cts.CancelAsync();
        try
        {
            await w.Run;
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        w.Cts.Dispose();
    }
}
