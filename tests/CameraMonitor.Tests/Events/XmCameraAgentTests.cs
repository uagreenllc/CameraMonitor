using System.Text.Json.Nodes;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Events;
using CameraMonitor.Api.Features.Streams;
using CameraMonitor.Api.Infrastructure.Db;
using CameraMonitor.Api.Infrastructure.Xm;
using CameraMonitor.Tests.Xm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CameraMonitor.Tests.Events;

public sealed class XmCameraAgentTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);
    private readonly FakeXmCamera _fake = new();
    private readonly ApiFactory _factory = new ApiFactory().WithoutHostedServices();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
    private readonly XmAgentStore _agents = new();
    private readonly CancellationTokenSource _cts = new();
    private Task _run = Task.CompletedTask;
    private CameraConfig _cam = null!;
    private XmCameraAgent _agent = null!;

    public async Task InitializeAsync()
    {
        await _fake.StartAsync();
        _cam = new CameraConfig
        {
            Id = "c1",
            Host = "127.0.0.1",
            DvripPort = _fake.Port,
            Lenses = [new() { Id = "fixed", Channel = 1 }, new() { Id = "ptz", Channel = 2, Ptz = true }],
        };
    }

    public async Task DisposeAsync()
    {
        await _cts.CancelAsync();
        await _run;
        await _fake.DisposeAsync();
        _factory.Dispose();
    }

    private XmCameraAgent NewAgent()
    {
        var sp = _factory.Services;
        return new XmCameraAgent(_cam, DvripCodec.SofiaHash(""), _agents, sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TileService>(), sp.GetRequiredService<IOptions<ServerOptions>>(), _clock, NullLogger.Instance);
    }

    private async Task<XmAgentState> StartConnected()
    {
        var agent = _agent = NewAgent();
        _run = Task.Run(() => agent.RunAsync(_cts.Token));
        var state = _agents.GetOrAdd("c1");
        await Until(() => state.Connected);
        return state;
    }

    private async Task<List<EventRow>> Events()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Events.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
    }

    private static Task Until(Func<bool> cond) => Until(() => Task.FromResult(cond()));

    private static async Task Until(Func<Task<bool>> cond)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!await cond() && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        (await cond()).Should().BeTrue("the condition should hold within 5 s");
    }

    /// <summary>Advances fake time in 100 ms steps until the condition holds (5 s wall clock).</summary>
    private async Task UntilAdvancing(Func<Task<bool>> cond)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!await cond() && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10);
        }

        (await cond()).Should().BeTrue("the condition should hold within 5 s");
    }

    [Fact]
    public async Task Connect_syncs_time_enables_detection_sets_gop_and_guards()
    {
        _fake.Configs["Detect"] = JsonNode.Parse("""{"MotionDetect":[{"Enable":false,"EventHandler":{"MsgtoNetEnable":false}},{"Enable":false}],"HumanDetection":[{"Enable":false},{"Enable":false}]}""")!;
        _fake.Configs["Simplify.Encode"] = JsonNode.Parse("""[{"MainFormat":{"Video":{"FPS":15,"GOP":4,"Compression":"H.264+","SmartH264":1}}},{"MainFormat":{"Video":{"FPS":15,"GOP":2,"Compression":"H.264"}}}]""")!;
        _fake.CameraTime = "2026-10-26 12:00:05";

        var state = await StartConnected();

        _fake.SetTimes.Should().Equal("2026-10-26 12:00:00");
        state.ClockDriftSeconds.Should().Be(5);
        var detect = _fake.Configs["Detect"];
        foreach (var arr in new[] { "MotionDetect", "HumanDetection" })
        {
            foreach (var ch in detect[arr]!.AsArray())
            {
                ch!["Enable"]!.GetValue<bool>().Should().BeTrue();
            }
        }

        detect["MotionDetect"]![0]!["EventHandler"]!["MsgtoNetEnable"]!.GetValue<bool>().Should().BeTrue();
        detect["MotionDetect"]![1]!.AsObject().ContainsKey("EventHandler").Should().BeFalse();
        var encode = _fake.Configs["Simplify.Encode"].AsArray();
        foreach (var ch in encode)
        {
            ch!["MainFormat"]!["Video"]!["GOP"]!.GetValue<int>().Should().Be(1);
            ch["MainFormat"]!["Video"]!["Compression"]!.GetValue<string>().Should().Be("H.264");
        }

        encode[0]!["MainFormat"]!["Video"]!["SmartH264"]!.GetValue<int>().Should().Be(0);
        _fake.Guarded.Should().BeTrue();
        state.Connected.Should().BeTrue();
        state.ConnectedSince.Should().Be(new DateTimeOffset(Now));
        state.Gop.Should().Be(1);
    }

    [Fact]
    public void ApplyEncode_reports_no_change_when_already_set()
    {
        var node = JsonNode.Parse("""[{"MainFormat":{"Video":{"FPS":15,"GOP":1,"Compression":"H.265"}}}]""")!;
        var before = node.ToJsonString();

        var result = XmCameraAgent.ApplyEncode(node, [0], out var changed);

        changed.Should().BeFalse();
        result.ToJsonString().Should().Be(before);
    }

    [Fact]
    public void ApplyDetection_skips_missing_arrays()
    {
        var node = JsonNode.Parse("""{"MotionDetect":[{"Enable":false}]}""")!;

        var result = XmCameraAgent.ApplyDetection(node, [0, 1], out var changed);

        changed.Should().BeTrue();
        result.ToJsonString().Should().Be("""{"MotionDetect":[{"Enable":true}]}""");
    }

    [Theory]
    [InlineData("HumanDetect", "human")]
    [InlineData("VideoMotion", "motion")]
    [InlineData("AppEventHumanDetectAlarm", "human")]
    [InlineData("VideoLoss", "videoloss")]
    [InlineData("../Foo:Bar", "foobar")]
    [InlineData("../:\\", "event")]
    public void MapEvent_maps_known_names(string xm, string expected) =>
        XmCameraAgent.MapEvent(xm).Should().Be(expected);

    [Fact]
    public async Task Alarm_start_creates_event_with_snapshot_and_stop_closes_it()
    {
        await StartConnected();

        await _fake.PushAlarmAsync(1, "HumanDetect", "Start");
        await Until(async () => (await Events()).Count == 1);

        var row = (await Events()).Single();
        row.CameraId.Should().Be("c1");
        row.LensId.Should().Be("ptz");
        row.Type.Should().Be("human");
        row.StartedUtc.Should().Be(Now);
        row.EndedUtc.Should().BeNull();
        row.SnapshotPath.Should().Be("events/c1/20261026/100000-ptz-human.jpg");
        var file = Path.Combine(_factory.DataDir, row.SnapshotPath!);
        File.Exists(file).Should().BeTrue();
        File.ReadAllBytes(file).Take(2).Should().Equal(0xFF, 0xD8);

        _clock.Advance(TimeSpan.FromSeconds(3));
        await _fake.PushAlarmAsync(1, "HumanDetect", "Stop");
        await Until(async () => (await Events()).Single().EndedUtc is not null);
        (await Events()).Single().EndedUtc.Should().Be(Now.AddSeconds(3));
    }

    [Fact]
    public async Task Alarm_start_still_creates_event_when_snapshot_cannot_be_written()
    {
        var blocker = Path.Combine(_factory.DataDir, "events", "c1");   // a file where the directory must go
        await StartConnected();
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
        await File.WriteAllTextAsync(blocker, "");

        await _fake.PushAlarmAsync(1, "HumanDetect", "Start");
        await Until(async () => (await Events()).Count == 1);

        var row = (await Events()).Single();
        row.LensId.Should().Be("ptz");
        row.SnapshotPath.Should().BeNull();
    }

    [Fact]
    public async Task Reconnect_cleanup_waits_for_an_alarm_handler_in_flight()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fake.BeforeReply = id =>
        {
            if (id != DvripMessages.Snapshot)
            {
                return Task.CompletedTask;
            }

            snapshotAsked.TrySetResult();
            return release.Task;
        };
        await StartConnected();

        await _fake.PushAlarmAsync(1, "HumanDetect", "Start");
        await snapshotAsked.Task.WaitAsync(TimeSpan.FromSeconds(5));   // the handler is grabbing its snapshot
        var cleanup = _agent.CloseOpenEventsAsync(CancellationToken.None);
        bool waited;
        try
        {
            await Task.Delay(100);
            waited = !cleanup.IsCompleted;
        }
        finally
        {
            release.SetResult();   // never leave the fake's connection blocked, or disposal hangs
        }

        waited.Should().BeTrue("the cleanup must wait for the in-flight alarm handler");
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));

        (await Events()).Should().ContainSingle().Which.EndedUtc.Should().NotBeNull("the handler's row was inserted before the cleanup closed it");
    }

    [Fact]
    public async Task Duplicate_start_does_not_create_second_open_event()
    {
        await StartConnected();

        await _fake.PushAlarmAsync(1, "HumanDetect", "Start");
        await _fake.PushAlarmAsync(1, "HumanDetect", "Start");
        await _fake.PushAlarmAsync(0, "VideoMotion", "Start");   // handled after both: alarms are processed in order
        await Until(async () => (await Events()).Any(e => e.LensId == "fixed"));

        var rows = await Events();
        rows.Should().HaveCount(2);
        rows.Count(e => e.LensId == "ptz" && e.Type == "human").Should().Be(1);
        rows.Single(e => e.LensId == "fixed").Type.Should().Be("motion");
    }

    [Fact]
    public async Task Reconnect_closes_open_events_and_increments_reconnects()
    {
        var state = await StartConnected();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Events.Add(new EventRow { CameraId = "c1", LensId = "ptz", Type = "human", StartedUtc = Now.AddMinutes(-1) });
            await db.SaveChangesAsync();
        }

        _fake.DropConnections();
        await UntilAdvancing(async () => _fake.Logins == 2 && state.Connected && (await Events()).Single().EndedUtc is not null);

        state.Reconnects.Should().Be(1);
    }

    [Fact]
    public async Task Ptz_throws_when_disconnected()
    {
        var agent = NewAgent();

        await agent.Invoking(a => a.PtzAsync("DirectionUp", 1, DvripClient.PtzStartPreset, 5, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        (await agent.SnapshotAsync(1, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Supervisor_starts_one_agent_per_xm_camera()
    {
        var cams = new CamerasOptions
        {
            Items =
            [
                _cam,
                new CameraConfig { Id = "r", Host = "h", Vendor = "rtsp", Lenses = [new() { Id = "m", RtspUrl = "rtsp://h/x" }] },
            ],
        };
        using var factory = new ApiFactory().WithCameras(cams);
        var store = factory.Services.GetRequiredService<XmAgentStore>();

        await Until(() => store.TryGet("c1", out var s) && s.Connected);

        store.Agent("c1")!.Client.Should().NotBeNull();
        store.Agent("r").Should().BeNull();
    }
}
