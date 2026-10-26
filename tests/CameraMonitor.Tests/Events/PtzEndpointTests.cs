using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Tests.Xm;
using FluentAssertions;

namespace CameraMonitor.Tests.Events;

public sealed class PtzEndpointTests : IAsyncLifetime
{
    private readonly FakeXmCamera _fake = new();
    private ApiFactory _factory = new();

    public async Task InitializeAsync() => await _fake.StartAsync();

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await _fake.DisposeAsync();
    }

    private static CamerasOptions Cams(int port) => new()
    {
        Items =
        [
            new()
            {
                Id = "c1",
                Name = "C1",
                Host = "127.0.0.1",
                DvripPort = port,
                Lenses = [new() { Id = "fixed", Channel = 1 }, new() { Id = "ptz", Channel = 2, Ptz = true }],
            },
        ],
    };

    private async Task<HttpClient> ConnectedAsync()
    {
        _factory = _factory.WithCameras(Cams(_fake.Port));
        var http = _factory.CreateClient();
        await Until(() => _fake.Logins == 1);
        return http;
    }

    private static async Task Until(Func<bool> cond)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("condition not met within 5 s");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Ptz_start_and_stop_reach_camera()
    {
        var http = await ConnectedAsync();
        (await http.PostAsJsonAsync("/api/ptz/c1/ptz", new { Command = "DirectionLeft", Action = "start" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await http.PostAsJsonAsync("/api/ptz/c1/ptz", new { Command = "DirectionLeft", Action = "stop" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await Until(() => _fake.PtzCommands.Count == 2);
        _fake.PtzCommands.Select(c => (int)c["Parameter"]!["Preset"]!).Should().Equal(65535, -1);
        _fake.PtzCommands.Should().OnlyContain(c => (int)c["Parameter"]!["Channel"]! == 1);
    }

    [Fact]
    public async Task Preset_goto_sends_GotoPreset()
    {
        var http = await ConnectedAsync();
        (await http.PostAsJsonAsync("/api/ptz/c1/ptz/preset", new { Action = "goto", Preset = 3 })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await Until(() => _fake.PtzCommands.Count == 1);
        _fake.PtzCommands[0]["Command"]!.ToString().Should().Be("GotoPreset");
        ((int)_fake.PtzCommands[0]["Parameter"]!["Preset"]!).Should().Be(3);
    }

    [Fact]
    public async Task Ptz_on_fixed_lens_is_404()
    {
        var http = await ConnectedAsync();
        (await http.PostAsJsonAsync("/api/ptz/c1/fixed", new { Command = "DirectionLeft", Action = "start" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ptz_unknown_command_is_400()
    {
        var http = await ConnectedAsync();
        (await http.PostAsJsonAsync("/api/ptz/c1/ptz", new { Command = "Explode", Action = "start" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await http.PostAsJsonAsync("/api/ptz/c1/ptz/preset", new { Action = "goto", Preset = 0 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ptz_when_agent_disconnected_is_503()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var closed = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _factory = _factory.WithCameras(Cams(closed));
        var http = _factory.CreateClient();
        var r = await http.PostAsJsonAsync("/api/ptz/c1/ptz", new { Command = "DirectionLeft", Action = "start" });
        r.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await r.Content.ReadAsStringAsync()).Should().Contain("Камера не підключена по DVRIP");
    }

    [Fact]
    public async Task Snapshot_endpoint_returns_jpeg()
    {
        var http = await ConnectedAsync();
        var r = await http.GetAsync("/api/snapshot/c1/fixed.jpg");
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        (await r.Content.ReadAsByteArrayAsync()).Should().Equal(_fake.Jpeg);
    }

    [Fact]
    public async Task Tile_uses_dvrip_when_configured()
    {
        _factory = _factory.WithSetting("Server:TileSource", "dvrip");
        var http = await ConnectedAsync();
        (await http.GetByteArrayAsync("/api/tile/c1/fixed.jpg")).Should().Equal(_fake.Jpeg);
    }
}
