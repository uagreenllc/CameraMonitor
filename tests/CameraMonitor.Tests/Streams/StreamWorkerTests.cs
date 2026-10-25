using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Streams;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CameraMonitor.Tests.Streams;

public sealed class StreamWorkerTests : IAsyncLifetime
{
    // Relative: ffmpeg tee paths must not contain a drive colon.
    private readonly string _dir = Path.Combine("cm-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly StreamStateStore _store = new();
    private readonly List<FakeFfmpegProcess> _procs = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly StreamSpec _spec;
    private Task _run = Task.CompletedTask;

    public StreamWorkerTests() =>
        _spec = new StreamSpec(new StreamKey("c", "main"), "rtsp://x/y", Path.Combine(_dir, "rec"), Path.Combine(_dir, "live"));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _cts.CancelAsync();
        await _run;
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private StreamState Start()
    {
        FfmpegFactory factory = (_, _) =>
        {
            var p = new FakeFfmpegProcess(_clock);
            lock (_procs) _procs.Add(p);
            return p;
        };
        var worker = new StreamWorker(_spec, factory, _store, new ServerOptions(), _clock, NullLogger.Instance);
        _run = Task.Run(() => worker.RunAsync(_cts.Token));
        return _store.GetOrAdd(_spec.Key);
    }

    private static async Task Until(Func<bool> cond)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(1);
        cond().Should().BeTrue("the condition should hold within 2 s");
    }

    private int Procs()
    {
        lock (_procs) return _procs.Count;
    }

    private FakeFfmpegProcess Proc(int i)
    {
        lock (_procs) return _procs[i];
    }

    /// <summary>Advances fake time one second at a time, letting the worker run between steps.</summary>
    private async Task Advance(int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            await Task.Delay(15);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        await Task.Delay(15);
    }

    [Fact]
    public async Task Progress_marks_stream_online()
    {
        var state = Start();
        await Until(() => Procs() == 1 && Proc(0).Started);
        Proc(0).ReportProgress();
        await Advance(1);
        await Until(() => state.Status == StreamStatus.Online);
        state.Generation.Should().Be(1);
        state.OnlineSince.Should().NotBeNull();
    }

    [Fact]
    public async Task No_progress_for_10s_kills_and_restarts()
    {
        var state = Start();
        await Until(() => Procs() == 1);
        await Advance(11);
        await Until(() => Proc(0).KillCalls == 1 && state.Restarts == 1);
        await Advance(1); // backoff(0) = 1 s
        await Until(() => Procs() == 2);
    }

    [Fact]
    public async Task No_video_for_60s_marks_offline()
    {
        var state = Start();
        var changes = new List<StreamStatus>();
        _store.StatusChanged += (_, prev) => changes.Add(prev);
        await Until(() => Procs() == 1);
        for (var i = 0; i < 120 && state.Status != StreamStatus.Offline; i++) await Advance(1);
        await Until(() => state.Status == StreamStatus.Offline);
        state.OfflineSince.Should().NotBeNull();
        changes.Should().Equal(StreamStatus.Starting);
    }

    [Fact]
    public async Task Progress_after_offline_marks_online_again()
    {
        var state = Start();
        var changes = new List<StreamStatus>();
        _store.StatusChanged += (_, prev) => changes.Add(prev);
        for (var i = 0; i < 120 && state.Status != StreamStatus.Offline; i++) await Advance(1);
        await Until(() => state.Status == StreamStatus.Offline);

        // Wait for the next ffmpeg start, then let it deliver video.
        var before = Procs();
        for (var i = 0; i < 70 && Procs() == before; i++) await Advance(1);
        await Until(() => Procs() > before);
        Proc(Procs() - 1).ReportProgress();
        await Advance(1);
        await Until(() => state.Status == StreamStatus.Online);
        changes.Should().Equal(StreamStatus.Starting, StreamStatus.Offline);
    }

    [Fact]
    public async Task Restart_delay_follows_backoff()
    {
        var state = Start();
        await Until(() => Procs() == 1);
        await Advance(11); // stall, kill; backoff(0) = 1 s
        await Until(() => state.Restarts == 1);
        await Advance(1);
        await Until(() => Procs() == 2);
        await Advance(11); // stall again; backoff(1) = 2 s
        await Until(() => state.Restarts == 2);
        await Advance(1);
        await Task.Delay(50);
        Procs().Should().Be(2, "only 1 s of the 2 s backoff has elapsed");
        await Advance(1);
        await Until(() => Procs() == 3);
    }

    [Fact]
    public async Task Run_clears_live_dir_on_start()
    {
        var stale = Path.Combine(_spec.LiveDir, "stale.ts");
        Directory.CreateDirectory(_spec.LiveDir);
        File.WriteAllText(stale, "x");
        Start();
        await Until(() => Procs() == 1);
        File.Exists(stale).Should().BeFalse();
        Directory.Exists(_spec.LiveDir).Should().BeTrue();
    }

    [Fact]
    public async Task Last_progress_survives_restart_and_offline_counts_from_it()
    {
        var state = Start();
        await Until(() => Procs() == 1);
        Proc(0).ReportProgress();
        await Advance(1);
        await Until(() => state.Status == StreamStatus.Online);
        var progressAt = state.LastProgressAt;
        progressAt.Should().NotBeNull();
        var t0 = _clock.GetUtcNow(); // ~ time of last progress (within the 1 s tick)

        await Advance(30);
        state.LastProgressAt.Should().Be(progressAt, "restarts must not erase the last real progress");
        while (_clock.GetUtcNow() - t0 < TimeSpan.FromSeconds(59)) await Advance(1);
        state.Status.Should().Be(StreamStatus.Online);
        while (_clock.GetUtcNow() - t0 < TimeSpan.FromSeconds(75)) await Advance(1); // offline is evaluated when a generation ends (~70 s with backoff)
        await Until(() => state.Status == StreamStatus.Offline);
        state.LastProgressAt.Should().Be(progressAt);
    }

    [Fact]
    public async Task Backoff_resets_after_each_generation_that_delivered_video()
    {
        var state = Start();
        for (var gen = 0; gen < 2; gen++)
        {
            await Until(() => Procs() == gen + 1);
            Proc(gen).ReportProgress();
            await Advance(1);
            Proc(gen).Exit(1);
            await Advance(1); // monitor tick notices the exit
            await Until(() => state.Restarts == gen + 1);
            await Advance(1); // backoff(0) = 1 s
        }

        await Until(() => Procs() == 3);
    }
}
