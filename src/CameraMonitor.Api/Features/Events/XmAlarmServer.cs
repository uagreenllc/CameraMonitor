using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Infrastructure.Xm;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Events;

/// <summary>
/// Fallback for firmware that does not push alarms on the control session: the camera connects to this server
/// (NetWork.AlarmServer) and sends AlarmInfo frames. The sender is matched to a camera by its IP.
/// </summary>
public sealed class XmAlarmServer(
    IOptionsMonitor<CamerasOptions> cameras, XmAgentStore agents, IOptions<ServerOptions> server, ILogger<XmAlarmServer> logger) : BackgroundService
{
    private static readonly TimeSpan ReadWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    private const int MaxBuffered = 1 << 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = server.Value.XmAlarmServerPort;
        if (port <= 0)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                logger.LogInformation("XM alarm server listening on port {Port}", port);
                try
                {
                    while (true)
                    {
                        var tcp = await listener.AcceptTcpClientAsync(stoppingToken);
                        _ = Task.Run(() => HandleAsync(tcp, stoppingToken), stoppingToken);
                    }
                }
                finally
                {
                    listener.Stop();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "XM alarm server failed, retrying in {Delay} s", RetryDelay.TotalSeconds);
                try
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task HandleAsync(TcpClient tcp, CancellationToken ct)
    {
        using var _ = tcp;
        try
        {
            var remote = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(ReadWindow);
            var stream = tcp.GetStream();
            var buffer = new List<byte>();
            var chunk = new byte[4096];
            try
            {
                while (true)
                {
                    var n = await stream.ReadAsync(chunk, deadline.Token);
                    if (n == 0)
                    {
                        return;
                    }

                    buffer.AddRange(chunk.AsSpan(0, n));
                    if (buffer.Count > MaxBuffered)
                    {
                        return;   // not an alarm sender
                    }

                    while (DvripCodec.TryReadFrame(CollectionsMarshal.AsSpan(buffer), out var frame, out var consumed))
                    {
                        var alarm = DvripClient.ParseAlarmInfo(DvripCodec.ParseJson(frame.Payload.Span));
                        buffer.RemoveRange(0, consumed);
                        if (alarm is not null)
                        {
                            await DispatchAsync(remote, alarm, ct);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // read window over
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }
        catch (Exception e)
        {
            logger.LogWarning("XM alarm server: connection failed: {Error}", e.Message);
        }
    }

    private async Task DispatchAsync(IPAddress? remote, AlarmInfo alarm, CancellationToken ct)
    {
        var cam = remote is null ? null : await FindCameraAsync(remote);
        if (cam is null || agents.Agent(cam.Id) is not { } agent)
        {
            logger.LogWarning("XM alarm server: alarm from unknown host {Host}", remote);
            return;
        }

        await agent.HandleAlarmAsync(alarm, ct);
    }

    private async Task<CameraConfig?> FindCameraAsync(IPAddress remote)
    {
        foreach (var cam in cameras.CurrentValue.Items.Where(c => c.Vendor == "xm"))
        {
            try
            {
                var addresses = IPAddress.TryParse(cam.Host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(cam.Host);
                if (addresses.Any(a => a.MapToIPv4().Equals(remote)))
                {
                    return cam;
                }
            }
            catch (SocketException)
            {
                // unresolvable host: cannot be the sender
            }
        }

        return null;
    }
}
