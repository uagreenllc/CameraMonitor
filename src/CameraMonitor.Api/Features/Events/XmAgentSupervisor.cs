using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Streams;
using CameraMonitor.Api.Infrastructure.Xm;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Events;

/// <summary>One <see cref="XmCameraAgent"/> per xm camera; restarts only the agents whose connection settings changed.</summary>
public sealed class XmAgentSupervisor(
    IOptionsMonitor<CamerasOptions> cameras, XmAgentStore agents, IServiceScopeFactory scopes, TileService tiles,
    IOptions<ServerOptions> server, TimeProvider clock, ILoggerFactory loggers) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private readonly Dictionary<AgentKey, (CancellationTokenSource Cts, Task Run)> _running = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _log = loggers.CreateLogger<XmAgentSupervisor>();
    private CancellationTokenSource? _pending;

    /// <summary>Everything an agent depends on; the hash keeps the password itself out of the key.</summary>
    private sealed record AgentKey(string Id, string Host, int Port, string User, string PasswordHash, string Lenses);

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
            _log.LogError(ex, "DVRIP supervisor failed to start agents");
        }

        _pending?.Cancel();
        await _gate.WaitAsync();
        try
        {
            foreach (var key in _running.Keys.ToList())
            {
                await StopAsync(key);
            }
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
                _log.LogError(ex, "DVRIP camera config reload failed");
            }
        });
    }

    private async Task ApplyAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var opts = cameras.CurrentValue;
            var wanted = opts.Items.Where(c => c.Vendor == "xm").ToDictionary(c => new AgentKey(
                c.Id, c.Host, c.DvripPort, c.User, DvripCodec.SofiaHash(CameraPasswords.Resolve(opts, c) ?? ""),
                string.Join(",", c.Lenses.Select(l => $"{l.Id}:{l.DvripChannel}"))));

            foreach (var key in _running.Keys.Except(wanted.Keys).ToList())
            {
                _log.LogInformation("Stopping DVRIP agent {Camera}", key.Id);
                await StopAsync(key);
                if (!wanted.Keys.Any(k => k.Id == key.Id))
                {
                    agents.Remove(key.Id);
                }
            }

            foreach (var (key, cam) in wanted.Where(w => !_running.ContainsKey(w.Key)))
            {
                _log.LogInformation("Starting DVRIP agent {Camera}", key.Id);
                var agent = new XmCameraAgent(cam, key.PasswordHash, agents, scopes, tiles, server, clock, loggers.CreateLogger<XmCameraAgent>());
                agents.Register(cam.Id, agent);
                var cts = new CancellationTokenSource();
                _running[key] = (cts, Task.Run(() => agent.RunAsync(cts.Token)));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopAsync(AgentKey key)
    {
        var (cts, run) = _running[key];
        _running.Remove(key);
        agents.Unregister(key.Id);
        await cts.CancelAsync();
        try
        {
            await run;   // RunAsync ends on cancellation without throwing
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "DVRIP agent {Camera} failed while stopping", key.Id);
        }

        cts.Dispose();
    }
}
