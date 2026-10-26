using System.Net.WebSockets;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Events;
using CameraMonitor.Api.Infrastructure;
using Microsoft.Extensions.Options;

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

        app.MapGet("/api/tile/{cam}/{lens}.jpg", async (string cam, string lens, TileService tiles, XmAgentStore agents, IOptions<ServerOptions> server,
            IOptionsMonitor<CamerasOptions> cameras, HttpContext http, CancellationToken ct) =>
        {
            if (!SafeName.IsSafe(cam) || !SafeName.IsSafe(lens)) return Results.BadRequest();
            byte[]? jpeg = null;
            // ponytail: measured on the real camera in Task 13; the default stays ffmpeg until then
            if (server.Value.TileSource == "dvrip" && agents.Agent(cam) is { Client: not null } agent
                && cameras.CurrentValue.Items.FirstOrDefault(c => c.Id == cam)?.Lenses.FirstOrDefault(x => x.Id == lens) is { } l)
                jpeg = await agent.SnapshotAsync(l.DvripChannel, ct);
            jpeg ??= await tiles.GetAsync(new StreamKey(cam, lens), ct);
            if (jpeg is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(jpeg, "image/jpeg");
        });
        // Map, not MapGet: HTTP/2 WebSockets arrive as CONNECT
        app.Map("/ws/live/{cam}/{lens}", async (string cam, string lens, HttpContext http, LowLatencyRelay relay,
            IOptionsMonitor<CamerasOptions> cameras, IOptions<ServerOptions> server, IHostApplicationLifetime lifetime) =>
        {
            if (!http.WebSockets.IsWebSocketRequest || !SafeName.IsSafe(cam) || !SafeName.IsSafe(lens)) return Results.BadRequest();
            var key = new StreamKey(cam, lens);
            using var sub = relay.Subscribe(key);
            // the relay's own lookup: only a stream it could have started is "over the limit", anything else is unknown
            if (sub is null && !StreamSupervisor.BuildSpecs(cameras.CurrentValue, server.Value).Any(s => s.Key == key)) return Results.NotFound();

            using var ws = await http.WebSockets.AcceptWebSocketAsync();
            if (sub is null)
            {
                await TryClose(ws, (WebSocketCloseStatus)1013, "relay limit");
                return Results.Empty;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            // Shutdown: end the feed so the client gets a close frame, and give it 5 s to answer; an open page must not stall the host.
            using var stopping = lifetime.ApplicationStopping.Register(() =>
            {
                sub.Dispose();
                cts.CancelAfter(TimeSpan.FromSeconds(5));
            });
            var receive = ReceiveUntilClosed(ws, cts);
            try
            {
                await foreach (var msg in sub.Messages.ReadAllAsync(cts.Token))
                    await ws.SendAsync(msg, WebSocketMessageType.Binary, endOfMessage: true, cts.Token);
                // source ended, this client fell too far behind, or the server is stopping
                if (lifetime.ApplicationStopping.IsCancellationRequested) await TryClose(ws, WebSocketCloseStatus.EndpointUnavailable, "server stopping");
                else await TryClose(ws, (WebSocketCloseStatus)1011, "stream ended");
                cts.CancelAfter(TimeSpan.FromSeconds(5));   // wait for the client's close, not forever
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
                // client closed or went away
                if (ws.State == WebSocketState.CloseReceived) await TryClose(ws, WebSocketCloseStatus.NormalClosure, null);
            }
            finally
            {
                await receive;
            }

            return Results.Empty;
        });
        return app;
    }

    private static async Task ReceiveUntilClosed(WebSocket ws, CancellationTokenSource stop)
    {
        var buf = new byte[256];
        try
        {
            while ((await ws.ReceiveAsync(buf, stop.Token)).MessageType != WebSocketMessageType.Close) { }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // aborted
        }
        finally
        {
            await stop.CancelAsync();
        }
    }

    private static async Task TryClose(WebSocket ws, WebSocketCloseStatus status, string? reason)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await ws.CloseOutputAsync(status, reason, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // client gone
        }
    }
}
