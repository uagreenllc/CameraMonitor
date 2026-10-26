using System.Net;
using System.Net.WebSockets;
using CameraMonitor.Api.Features.Cameras;

namespace CameraMonitor.Tests.Streams;

public class LiveWsEndpointTests
{
    private static readonly Uri Live = new("ws://localhost/ws/live/sim1/main");

    private static ApiFactory Factory(FakeRelaySource source) => new ApiFactory().WithRelaySource(source.Run).WithCameras(new CamerasOptions
    {
        Items = [new CameraConfig { Id = "sim1", Host = "sim", Vendor = "rtsp", Lenses = [new() { Id = "main", RtspUrl = "rtsp://u:secret@sim:8554/main" }] }],
    });

    private static async Task<byte[]> Receive(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var r = await ws.ReceiveAsync(buf, ct);
        r.MessageType.Should().Be(WebSocketMessageType.Binary);
        r.EndOfMessage.Should().BeTrue();
        return buf[..r.Count];
    }

    [Fact]
    public async Task Ws_streams_init_then_fragments()
    {
        var source = new FakeRelaySource(3);
        using var factory = Factory(source);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using var ws = await factory.Server.CreateWebSocketClient().ConnectAsync(Live, cts.Token);

        (await Receive(ws, cts.Token)).Should().Equal(FakeRelaySource.Init);
        (await Receive(ws, cts.Token)).Should().Equal(FakeRelaySource.Fragment(0));
        source.Args.Should().Contain("rtsp://u:secret@sim:8554/main");
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
    }

    [Fact]
    public async Task Ws_source_exit_closes_with_1011()
    {
        using var factory = Factory(new FakeRelaySource(10, exitAfter: 2));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using var ws = await factory.Server.CreateWebSocketClient().ConnectAsync(Live, cts.Token);
        WebSocketReceiveResult r;
        do r = await ws.ReceiveAsync(new byte[64 * 1024], cts.Token);
        while (r.MessageType != WebSocketMessageType.Close);

        ws.CloseStatus.Should().Be((WebSocketCloseStatus)1011);
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
    }

    [Fact]
    public async Task Ws_unknown_lens_is_404()
    {
        using var factory = Factory(new FakeRelaySource(3));

        var act = () => factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws/live/sim1/nope"), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>().WithMessage("*404*");
    }

    [Fact]
    public async Task Ws_over_limit_closes_with_1013()
    {
        using var factory = Factory(new FakeRelaySource(3)).WithSetting("Server:MaxRelays", "0");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using var ws = await factory.Server.CreateWebSocketClient().ConnectAsync(Live, cts.Token);
        var r = await ws.ReceiveAsync(new byte[1024], cts.Token);

        r.MessageType.Should().Be(WebSocketMessageType.Close);
        ws.CloseStatus.Should().Be((WebSocketCloseStatus)1013);
    }

    [Fact]
    public async Task Ws_rejects_traversal_names()
    {
        using var factory = Factory(new FakeRelaySource(3));

        var resp = await factory.CreateClient().GetAsync("/ws/live/a/..%2Fb");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var ws = () => factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws/live/a/..%2Fb"), CancellationToken.None);
        await ws.Should().ThrowAsync<Exception>().WithMessage("*400*");
    }
}
