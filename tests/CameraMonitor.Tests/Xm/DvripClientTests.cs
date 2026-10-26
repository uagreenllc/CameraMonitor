using System.Text.Json.Nodes;
using CameraMonitor.Api.Infrastructure.Xm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CameraMonitor.Tests.Xm;

public sealed class DvripClientTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly FakeXmCamera _fake = new();
    private readonly List<DvripClient> _clients = [];

    public Task InitializeAsync() => _fake.StartAsync();

    public async Task DisposeAsync()
    {
        foreach (var c in _clients)
        {
            await c.DisposeAsync();
        }

        await _fake.DisposeAsync();
    }

    private DvripClient NewClient(TimeProvider? clock = null)
    {
        var c = new DvripClient("127.0.0.1", _fake.Port, "admin", DvripCodec.SofiaHash(""), clock ?? TimeProvider.System, NullLogger.Instance);
        _clients.Add(c);
        return c;
    }

    private async Task<DvripClient> ConnectedClient(TimeProvider? clock = null)
    {
        var c = NewClient(clock);
        await c.ConnectAsync(CancellationToken.None);
        return c;
    }

    [Fact]
    public async Task Login_sets_session_and_alive_interval()
    {
        var c = await ConnectedClient();

        c.Connected.Should().BeTrue();
        c.Session.Should().Be(0x42u);
        c.AliveIntervalSeconds.Should().Be(21);
        _fake.Logins.Should().Be(1);
    }

    [Fact]
    public async Task Wrong_password_throws_DvripException_203()
    {
        _fake.PasswordHash = DvripCodec.SofiaHash("other");
        var c = NewClient();

        var ex = await c.Invoking(x => x.ConnectAsync(CancellationToken.None)).Should().ThrowAsync<DvripException>();

        ex.Which.Ret.Should().Be(203);
        c.Connected.Should().BeFalse();
    }

    [Fact]
    public async Task Request_times_out_when_camera_is_silent()
    {
        var clock = new FakeTimeProvider();
        _fake.Ignore.Add(DvripMessages.ConfigGet);
        var c = await ConnectedClient(clock);

        var task = c.GetConfigAsync("General", CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(100); // gives a wrongly-early timeout the chance to surface before the assertion
        task.IsCompleted.Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(2));
        await task.Awaiting(t => t).Should().ThrowAsync<TimeoutException>();
        c.Connected.Should().BeTrue();
    }

    [Fact]
    public async Task Alarm_push_raises_event()
    {
        var c = await ConnectedClient();
        var alarms = new List<AlarmInfo>();
        c.Alarm += a =>
        {
            lock (alarms)
            {
                alarms.Add(a);
            }
        };

        await _fake.PushAlarmAsync(1, "HumanDetect", "Start");
        await Until(() => Count(alarms) == 1);

        alarms[0].Channel.Should().Be(1);
        alarms[0].Event.Should().Be("HumanDetect");
        alarms[0].Status.Should().Be("Start");
        alarms[0].StartTimeLocal.Should().NotBeNull();

        // A push that lands between a request and its reply must not be taken for the reply.
        _fake.Configs["General"] = new JsonObject { ["A"] = 1 };
        _fake.BeforeReply = id => id == DvripMessages.ConfigGet ? _fake.PushAlarmAsync(0, "MotionDetect", "Stop") : Task.CompletedTask;

        var general = await c.GetConfigAsync("General", CancellationToken.None);

        JsonNode.DeepEquals(general, _fake.Configs["General"]).Should().BeTrue();
        await Until(() => Count(alarms) == 2);
        alarms[1].Should().BeEquivalentTo(new { Channel = 0, Event = "MotionDetect", Status = "Stop" });
    }

    [Fact]
    public async Task GetConfig_returns_named_section()
    {
        _fake.Configs["Detect"] = JsonNode.Parse("""{"MotionDetect":[{"Enable":false}]}""")!;
        var c = await ConnectedClient();

        var detect = await c.GetConfigAsync("Detect", CancellationToken.None);
        JsonNode.DeepEquals(detect, _fake.Configs["Detect"]).Should().BeTrue();

        var updated = JsonNode.Parse("""{"MotionDetect":[{"Enable":true}]}""")!;
        await c.SetConfigAsync("Detect", updated, CancellationToken.None);
        JsonNode.DeepEquals(_fake.Configs["Detect"], updated).Should().BeTrue();

        // The fake answers an unknown name with Ret 607 "Configuration does not exist".
        (await c.Invoking(x => x.GetConfigAsync("Missing", CancellationToken.None)).Should().ThrowAsync<DvripException>())
            .Which.Ret.Should().Be(607);
    }

    [Fact]
    public async Task GetConfig_rejects_a_reply_for_another_section()
    {
        _fake.Configs["Detect"] = JsonNode.Parse("""{"MotionDetect":[{"Enable":false}]}""")!;
        _fake.ConfigReplyName = "General";
        var c = await ConnectedClient();

        (await c.Invoking(x => x.GetConfigAsync("Detect", CancellationToken.None)).Should().ThrowAsync<DvripException>())
            .Which.Message.Should().StartWith("stale reply");
    }

    [Fact]
    public async Task GetTime_and_SetTime_round_trip()
    {
        var c = await ConnectedClient();

        var time = await c.GetTimeAsync(CancellationToken.None);
        time.Should().Be(new DateTime(2026, 10, 26, 12, 0, 0));
        time.Kind.Should().Be(DateTimeKind.Unspecified);

        await c.SetTimeAsync(new DateTime(2026, 10, 26, 14, 3, 10), CancellationToken.None);
        _fake.SetTimes.Should().Equal("2026-10-26 14:03:10");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Snapshot_returns_jpeg_bytes(int frames)
    {
        _fake.JpegFrames = frames;
        var c = await ConnectedClient();

        var jpeg = await c.SnapshotAsync(0, CancellationToken.None);

        jpeg.Should().Equal(_fake.Jpeg);
        await c.KeepAliveAsync(CancellationToken.None); // the connection is still usable afterwards
    }

    [Fact]
    public async Task Ptz_sends_python_dvr_payload()
    {
        var c = await ConnectedClient();

        await c.PtzAsync("DirectionLeft", 1, DvripClient.PtzStartPreset, 5, CancellationToken.None);

        var cmd = _fake.PtzCommands.Should().ContainSingle().Subject;
        cmd["Command"]!.GetValue<string>().Should().Be("DirectionLeft");
        cmd["Parameter"]!["Channel"]!.GetValue<int>().Should().Be(1);
        cmd["Parameter"]!["Preset"]!.GetValue<int>().Should().Be(65535);
        cmd["Parameter"]!["Pattern"]!.GetValue<string>().Should().Be("SetBegin");
        cmd["Parameter"]!["Step"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public async Task QueryFiles_pages_by_64()
    {
        var start = new DateTime(2026, 10, 26, 0, 0, 0);
        for (var i = 0; i < 70; i++)
        {
            AddFile(0, start.AddMinutes(i), 1);
        }

        AddFile(1, start.AddMinutes(5), 1);              // other channel
        AddFile(0, start.AddDays(-1), 1);                // before the range
        AddFile(0, start.AddDays(1), 1);                 // after the range
        var c = await ConnectedClient();

        var files = await c.QueryFilesAsync(0, start, start.AddHours(12), CancellationToken.None);

        files.Should().HaveCount(70);
        files.Select(f => f.FileName).Should().OnlyHaveUniqueItems();
        files.Should().OnlyContain(f => f.Channel == 0 && f.BeginLocal >= start && f.BeginLocal < start.AddHours(12));
        files[0].EndLocal.Should().Be(start.AddMinutes(1));
        files[0].LengthKb.Should().Be(0x1F4);
        _fake.FileQueries.Should().Be(2);
    }

    [Fact]
    public async Task QueryFiles_returns_empty_when_camera_has_none()
    {
        var c = await ConnectedClient();

        var files = await c.QueryFilesAsync(0, new DateTime(2026, 10, 26), new DateTime(2026, 10, 27), CancellationToken.None);

        files.Should().BeEmpty();
    }

    [Fact]
    public async Task Download_reassembles_chunks_and_sends_stop()
    {
        var bytes = new byte[100_000];
        new Random(7).NextBytes(bytes);
        var entry = new XmFileEntry("/idea0/2026-10-26/001/00.00.00-00.01.00[R][@1][0].h264",
            new DateTime(2026, 10, 26, 0, 0, 0), new DateTime(2026, 10, 26, 0, 1, 0), 98, 0);
        _fake.Files.Add((entry, bytes));
        _fake.DownloadChunk = 4096;
        var c = await ConnectedClient();

        using var dest = new MemoryStream();
        await c.DownloadAsync(entry, dest, CancellationToken.None);

        dest.ToArray().Should().Equal(bytes);
        _fake.DownloadStopped.Should().BeTrue();
        await c.KeepAliveAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Slow_download_reader_stops_reading_the_socket()
    {
        var bytes = new byte[8_000_000];   // ~2000 frames of 4 KB
        var entry = new XmFileEntry("/idea0/2026-10-26/001/00.00.00-00.01.00[R][@1][0].h264",
            new DateTime(2026, 10, 26, 0, 0, 0), new DateTime(2026, 10, 26, 0, 1, 0), 7812, 0);
        _fake.Files.Add((entry, bytes));
        var c = await ConnectedClient();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var download = c.DownloadAsync(entry, new GatedStream(gate.Task), CancellationToken.None);

        await Task.Delay(1000);   // the camera sends at loopback speed while the destination is blocked
        c.QueuedFrames.Should().BeLessThanOrEqualTo(DvripClient.MaxQueuedFrames + 64, "a throttled download must hold the camera back, not buffer the file");

        gate.SetResult();
        await download.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class GatedStream(Task gate) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => await gate.WaitAsync(ct);
    }

    [Fact]
    public async Task Closed_raised_when_camera_drops_connection()
    {
        var c = await ConnectedClient();
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Closed += ex => closed.TrySetResult(ex);

        _fake.DropConnections();

        (await closed.Task.WaitAsync(Wait)).Should().NotBeNull();
        c.Connected.Should().BeFalse();
        await c.Invoking(x => x.KeepAliveAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Dispose_fails_inflight_request_and_raises_Closed_with_null()
    {
        _fake.Ignore.Add(DvripMessages.KeepAlive);
        var c = await ConnectedClient();
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Closed += ex => closed.TrySetResult(ex);
        var pending = c.KeepAliveAsync(CancellationToken.None);
        await Until(() => _fake.KeepAlives == 1); // the request is on the wire, now waiting for its reply

        await c.DisposeAsync();

        await pending.Awaiting(t => t).Should().ThrowAsync<InvalidOperationException>();
        (await closed.Task.WaitAsync(Wait)).Should().BeNull();
        c.Connected.Should().BeFalse();
    }

    [Fact]
    public async Task Throwing_alarm_handler_does_not_break_the_client()
    {
        var c = await ConnectedClient();
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Alarm += _ => throw new InvalidOperationException("boom");
        c.Alarm += _ => seen.TrySetResult();

        await _fake.PushAlarmAsync(0, "HumanDetect", "Start");

        await seen.Task.WaitAsync(Wait);
        await c.KeepAliveAsync(CancellationToken.None);
        _fake.KeepAlives.Should().Be(1);
    }

    private void AddFile(int channel, DateTime begin, int minutes)
    {
        var entry = new XmFileEntry($"/idea0/{begin:yyyy-MM-dd}/00{channel}/{begin:HH.mm.ss}.h264", begin, begin.AddMinutes(minutes), 0x1F4, channel);
        _fake.Files.Add((entry, [1, 2, 3]));
    }

    private static int Count<T>(List<T> list)
    {
        lock (list)
        {
            return list.Count;
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the condition should hold within {0}", Wait);
            await Task.Delay(10);
        }
    }
}
