using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Events;
using CameraMonitor.Api.Infrastructure.Db;
using CameraMonitor.Api.Infrastructure.Xm;
using CameraMonitor.Tests.Xm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Tests.Events;

public sealed class XmAlarmServerTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<bool> UntilAsync(Func<Task<bool>> cond, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!await cond() && sw.ElapsedMilliseconds < ms) await Task.Delay(10);
        return await cond();
    }

    private static async Task<List<EventRow>> Events(ApiFactory f)
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Events.AsNoTracking().ToListAsync();
    }

    private static async Task SendAlarm(int port)
    {
        var body = new JsonObject
        {
            ["Name"] = "AlarmInfo",
            ["AlarmInfo"] = new JsonObject { ["Channel"] = 0, ["Event"] = "VideoMotion", ["Status"] = "Start", ["StartTime"] = "2026-10-26 12:00:00" },
        };
        var frame = DvripCodec.EncodeJson(0, 0, 1504, body);
        // the listener starts asynchronously with the host: retry the connect
        TcpClient? tcp = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (tcp is null)
        {
            var attempt = new TcpClient();
            try { await attempt.ConnectAsync(IPAddress.Loopback, port); tcp = attempt; }
            catch (SocketException) when (sw.ElapsedMilliseconds < 5000) { attempt.Dispose(); await Task.Delay(20); }
        }

        using (tcp)
        {
            await tcp!.GetStream().WriteAsync(frame);
        }
    }

    private static ApiFactory Factory(string host, int port) =>
        new ApiFactory().WithSetting("Server:XmAlarmServerPort", port.ToString())
            .WithCameras(new CamerasOptions { Items = [new CameraConfig { Id = "c1", Host = host, DvripPort = FreePort(), Lenses = [new() { Id = "fixed", Channel = 1 }] }] });

    [Fact]
    public async Task Alarm_frame_from_known_host_creates_event()
    {
        var port = FreePort();
        using var f = Factory("127.0.0.1", port);
        _ = f.CreateClient();
        await SendAlarm(port);

        (await UntilAsync(async () => (await Events(f)).Count == 1)).Should().BeTrue();
        var row = (await Events(f)).Single();
        row.LensId.Should().Be("fixed");
        row.Type.Should().Be("motion");
        row.SnapshotPath.Should().BeNull();
    }

    [Fact]
    public async Task Alarm_frame_from_unknown_host_is_ignored()
    {
        var port = FreePort();
        using var f = Factory("10.0.0.9", port);
        _ = f.CreateClient();
        await SendAlarm(port);

        await Task.Delay(500);   // negative assertion: give the server time to (wrongly) act
        (await Events(f)).Should().BeEmpty();
    }

    [Fact]
    public async Task Agent_configures_alarm_server_on_connect()
    {
        await using var fake = new FakeXmCamera();
        await fake.StartAsync();
        fake.Configs["NetWork.AlarmServer"] = JsonNode.Parse("""{"Address":"0.0.0.0","Enable":false,"Port":15002,"Protocol":"TCP"}""")!;
        using var f = new ApiFactory().WithoutHostedServices()
            .WithSetting("Server:XmAlarmServerHost", "192.168.50.2").WithSetting("Server:XmAlarmServerPort", "15002");
        var sp = f.Services;
        var agents = new XmAgentStore();
        var cam = new CameraConfig { Id = "c1", Host = "127.0.0.1", DvripPort = fake.Port, Lenses = [new() { Id = "fixed", Channel = 1 }] };
        var agent = new XmCameraAgent(cam, DvripCodec.SofiaHash(""), agents, sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<CameraMonitor.Api.Features.Streams.TileService>(), sp.GetRequiredService<IOptions<ServerOptions>>(),
            TimeProvider.System, NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => agent.RunAsync(cts.Token));

        var state = agents.GetOrAdd("c1");
        (await UntilAsync(() => Task.FromResult(state.Connected))).Should().BeTrue();
        fake.Configs["NetWork.AlarmServer"]!["Enable"]!.GetValue<bool>().Should().BeTrue();
        fake.Configs["NetWork.AlarmServer"]!["Address"]!.GetValue<string>().Should().Be("192.168.50.2");
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Retry_after_a_bind_failure_waits_on_the_injected_clock()
    {
        var port = FreePort();
        var blocker = new TcpListener(IPAddress.Any, port);
        blocker.Start();
        using var f = new ApiFactory().WithoutHostedServices().WithSetting("Server:XmAlarmServerPort", port.ToString());
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var server = ActivatorUtilities.CreateInstance<XmAlarmServer>(f.Services, (TimeProvider)clock);
        await server.StartAsync(CancellationToken.None);   // the bind fails: the port is taken
        blocker.Stop();
        try
        {
            await Task.Delay(300);
            (await CanConnect(port)).Should().BeFalse("the retry waits for the fake clock");

            clock.Advance(TimeSpan.FromSeconds(10));

            (await UntilAsync(() => CanConnect(port))).Should().BeTrue();
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<bool> CanConnect(int port)
    {
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
