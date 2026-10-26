using System.Diagnostics;
using CameraMonitor.Api.Features.Archive;
using CameraMonitor.Api.Features.Backfill;
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

namespace CameraMonitor.Tests.Backfill;

[Collection("real-ffmpeg")]
public sealed class SdBackfillTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 7, 1, 12, 30, 0, DateTimeKind.Utc);
    private readonly FakeXmCamera _fake = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
    private ApiFactory _factory = null!;
    private SdBackfill _sd = null!;
    private BackfillStatus _status = null!;

    public async Task InitializeAsync()
    {
        await _fake.StartAsync();
        var cam = new CameraConfig
        {
            Id = "c1",
            Host = "127.0.0.1",
            DvripPort = _fake.Port,
            Password = "",
            Lenses = [new() { Id = "ptz", Channel = 2, Ptz = true }],
        };
        _factory = new ApiFactory().WithoutHostedServices().WithCameras(new CamerasOptions { Items = [cam] })
            .WithSetting("Server:TimeZone", "Europe/Kyiv")
            .WithSetting("Server:SdMaxKbps", "0");   // the throttle waits on the fake clock
        var sp = _factory.Services;
        var agents = sp.GetRequiredService<XmAgentStore>();
        agents.Register("c1", new XmCameraAgent(cam, DvripCodec.SofiaHash(""), agents, sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TileService>(), sp.GetRequiredService<IOptions<ServerOptions>>(), _clock, NullLogger.Instance));
        _sd = ActivatorUtilities.CreateInstance<SdBackfill>(sp, (TimeProvider)_clock);
        _status = sp.GetRequiredService<BackfillStatus>();
    }

    public async Task DisposeAsync()
    {
        _sd.Dispose();
        await _fake.DisposeAsync();
        _factory.Dispose();
    }

    private static DateTime At(int m, int s = 0) => new(2026, 7, 1, 12, m, s, DateTimeKind.Utc);

    private async Task Seed(params (DateTime Start, int Ms)[] rows)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var (s, ms) in rows)
            db.Segments.Add(new SegmentRow { CameraId = "c1", LensId = "ptz", StartUtc = s, DurationMs = ms, Path = $"rec/c1/ptz/live/{s:HHmmss}.ts" });
        await db.SaveChangesAsync();
    }

    private async Task<List<SegmentRow>> SdRows()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Segments.AsNoTracking()
            .Where(s => s.Source == "sd").OrderBy(s => s.StartUtc).ToListAsync();
    }

    private static void Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true })!;
        p.WaitForExit();
        p.ExitCode.Should().Be(0);
    }

    /// <summary>The 15-fps test stream of Task 8 (raw H.264), <paramref name="seconds"/> long.</summary>
    private static byte[] RawH264(int seconds)
    {
        var dir = $"./cm-tests-data/sdsrc-{Guid.NewGuid():N}";
        Directory.CreateDirectory(dir);
        try
        {
            var raw = $"{dir}/raw.264";
            Run("ffmpeg", $"-nostdin -loglevel error -f lavfi -i testsrc2=size=320x240:rate=15 -t {seconds} -c:v libx264 -preset veryfast -g 15 -bf 0 -pix_fmt yuv420p -f h264 {raw}");
            return File.ReadAllBytes(raw);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A card file on channel 1 wrapped as one Xiongmai I-frame stamped with <paramref name="iFrameTime"/>.</summary>
    private void AddFile(byte[] raw, DateTime iFrameTime, DateTime beginLocal, DateTime endLocal)
    {
        var bytes = XmTestStream.IFrame(iFrameTime, 15, 320, 240, raw);
        _fake.Files.Add((new XmFileEntry($"/idea0/{beginLocal:yyyy-MM-dd}/001/{beginLocal:HH.mm.ss}-{endLocal:HH.mm.ss}[R][@1][0].h264",
            beginLocal, endLocal, bytes.Length / 1024, 1), bytes));
    }

    /// <summary>The 25-s stream as the card file 15:05:00–15:05:25 local.</summary>
    private void AddSdFile(DateTime iFrameTime) =>
        AddFile(RawH264(25), iFrameTime, new DateTime(2026, 7, 1, 15, 5, 0), new DateTime(2026, 7, 1, 15, 5, 25));

    [FfmpegFact]
    public async Task Gap_is_filled_from_sd_with_true_start_and_source_sd()
    {
        await Seed((At(0), 300_000), (At(10), 1_200_000));
        AddSdFile(new DateTime(2026, 7, 1, 15, 5, 0));

        await _sd.RunOnceAsync("c1", CancellationToken.None);

        var rows = await SdRows();
        rows.Select(r => r.StartUtc).Should().Equal(At(5), At(5, 10), At(5, 20));
        rows.Select(r => r.DurationMs).Should().Equal(new[] { 10_000, 10_000, 5_000 }, (a, b) => Math.Abs(a - b) <= 200);
        rows.Select(r => r.Discontinuity).Should().Equal(true, false, false);
        rows[0].Path.Should().Be("rec/c1/ptz/20260701/12/120500.ts");
        rows.Should().OnlyContain(r => File.Exists(Path.Combine(_factory.DataDir, r.Path)) && r.Bytes > 0);
        var st = _status.GetOrAdd("c1");
        st.SegmentsAdded.Should().Be(3);
        st.FilesDone.Should().Be(1);
        st.LastError.Should().BeNull();
        st.LastRunUtc.Should().Be(new DateTimeOffset(Now));
        Directory.EnumerateDirectories(Path.Combine(_factory.DataDir, "tmp", "sd", "c1", "ptz")).Should().BeEmpty();
    }

    [FfmpegFact]
    public async Task Segments_overlapping_live_rows_are_skipped()
    {
        await Seed((At(0), 300_000), (At(5), 10_000), (At(10), 1_200_000));
        AddSdFile(new DateTime(2026, 7, 1, 15, 5, 0));

        await _sd.RunOnceAsync("c1", CancellationToken.None);

        var rows = await SdRows();
        rows.Select(r => r.StartUtc).Should().Equal(At(5, 10), At(5, 20));
        rows[0].Discontinuity.Should().BeTrue();
        _status.GetOrAdd("c1").SegmentsAdded.Should().Be(2);
    }

    [FfmpegFact]
    public async Task Segment_indexed_by_someone_else_meanwhile_keeps_its_file()
    {
        await Seed((At(0), 300_000), (At(10), 1_200_000));
        using (var scope = _factory.Services.CreateScope())
        {
            // As if DirectoryScanner had indexed the moved file at its mtime before our insert.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Segments.Add(new SegmentRow { CameraId = "c1", LensId = "ptz", StartUtc = At(20), DurationMs = 10_000, Path = "rec/c1/ptz/20260701/12/120500.ts" });
            await db.SaveChangesAsync();
        }

        AddSdFile(new DateTime(2026, 7, 1, 15, 5, 0));

        await _sd.RunOnceAsync("c1", CancellationToken.None);

        File.Exists(Path.Combine(_factory.DataDir, "rec/c1/ptz/20260701/12/120500.ts")).Should().BeTrue("the other row points at it");
        (await SdRows()).Select(r => r.StartUtc).Should().Equal(At(5, 10), At(5, 20));
        _status.GetOrAdd("c1").LastError.Should().BeNull();
    }

    [FfmpegFact]
    public async Task At_most_MaxFilesPerRun_files_per_run_newest_first()
    {
        await Seed((new DateTime(2026, 7, 1, 11, 0, 0, DateTimeKind.Utc), 3_600_000));   // the gap is [12:00, 12:28)
        var raw = RawH264(10);
        for (var i = 0; i < 25; i++)
        {
            var local = new DateTime(2026, 7, 1, 15, i, 0);   // 12:i UTC
            AddFile(raw, local, local, local.AddSeconds(10));
        }

        await _sd.RunOnceAsync("c1", CancellationToken.None);

        var st = _status.GetOrAdd("c1");
        st.FilesDone.Should().Be(SdBackfill.MaxFilesPerRun);
        (await SdRows()).Select(r => r.StartUtc.Minute).Distinct().Should().Equal(Enumerable.Range(5, 20));
        _sd.Pending.Should().Contain("c1", "a run cut short by the cap continues soon");

        await _sd.RunOnceAsync("c1", CancellationToken.None);

        st.FilesDone.Should().Be(25);
        (await SdRows()).Select(r => r.StartUtc.Minute).Distinct().Should().Equal(Enumerable.Range(0, 25));
        st.LastError.Should().BeNull();
    }

    [FfmpegFact]
    public async Task File_with_timestamp_outside_gap_is_skipped()
    {
        await Seed((At(0), 300_000), (At(10), 1_200_000));
        AddSdFile(new DateTime(2000, 1, 1, 0, 0, 0));

        await _sd.RunOnceAsync("c1", CancellationToken.None);

        (await SdRows()).Should().BeEmpty();
        var st = _status.GetOrAdd("c1");
        st.FilesDone.Should().Be(0);
        st.LastError.Should().BeNull();
        Directory.EnumerateDirectories(Path.Combine(_factory.DataDir, "tmp", "sd", "c1", "ptz")).Should().BeEmpty();
    }

    [Fact]
    public async Task Gap_without_files_is_marked_and_not_retried()
    {
        await _sd.RunOnceAsync("c1", CancellationToken.None);
        var st = _status.GetOrAdd("c1");
        st.GapsNoFiles.Should().Be(1);
        st.LastError.Should().BeNull();
        var queries = _fake.FileQueries;
        queries.Should().BeGreaterThan(0);

        await _sd.RunOnceAsync("c1", CancellationToken.None);

        _fake.FileQueries.Should().Be(queries);
        st.GapsNoFiles.Should().Be(1);
    }

    [Fact]
    public async Task Shifted_leading_gap_inside_an_empty_one_is_not_retried()
    {
        await Seed((At(0), 38 * 60_000));   // the only gap is the leading one, [now − 3 d, 12:00)
        await _sd.RunOnceAsync("c1", CancellationToken.None);
        var queries = _fake.FileQueries;

        _clock.Advance(SdBackfill.Interval);   // the window start moves on, the gap end does not
        await _sd.RunOnceAsync("c1", CancellationToken.None);

        _fake.FileQueries.Should().Be(queries);
        _status.GetOrAdd("c1").GapsNoFiles.Should().Be(1);
    }

    [Fact]
    public async Task Range_evicted_by_retention_is_not_queried_again()
    {
        await Seed((At(0), 300_000), (At(10), 1_200_000));   // gaps: the leading one [now − 3 d, 12:00) and [12:05, 12:10)
        _factory.Services.GetRequiredService<RetentionStatus>().EvictedThroughUtc = At(0);

        await _sd.RunOnceAsync("c1", CancellationToken.None);

        _fake.FileQueries.Should().BeGreaterThan(0);
        _fake.FileQueryRanges.Should().OnlyContain(r => r.Begin >= new DateTime(2026, 7, 1, 14, 59, 0),
            "the evicted range before 12:00 UTC (15:00 local) must not be refilled");
    }

    [FfmpegFact]
    public async Task Corrupt_file_does_not_block_the_gap_and_is_given_up_after_three_attempts()
    {
        await Seed((At(0), 300_000), (At(10), 1_200_000));
        AddSdFile(new DateTime(2026, 7, 1, 15, 5, 0));
        var corrupt = "/idea0/2026-07-01/001/15.06.00-15.06.30[R][@1][0].h264";
        _fake.Files.Add((new XmFileEntry(corrupt, new DateTime(2026, 7, 1, 15, 6, 0), new DateTime(2026, 7, 1, 15, 6, 30), 1, 1),
            [0, 0, 1, 0x42, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]));   // unknown frame magic, newest: tried first

        await _sd.RunOnceAsync("c1", CancellationToken.None);

        (await SdRows()).Select(r => r.StartUtc).Should().Equal(At(5), At(5, 10), At(5, 20));
        for (var i = 0; i < 3; i++)
        {
            await _sd.RunOnceAsync("c1", CancellationToken.None);
        }

        _fake.Downloads.Count(d => d == corrupt).Should().Be(3);
    }

    [FfmpegFact]
    public async Task Segment_times_are_scaled_to_the_card_file_duration()
    {
        await Seed((At(0), 300_000), (At(10), 1_200_000));
        // 25 s of frames in a file the card says lasted 30 s: the camera delivered fewer frames than its fps byte says
        AddFile(RawH264(25), new DateTime(2026, 7, 1, 15, 5, 0), new DateTime(2026, 7, 1, 15, 5, 0), new DateTime(2026, 7, 1, 15, 5, 30));

        await _sd.RunOnceAsync("c1", CancellationToken.None);

        var rows = await SdRows();
        rows.Select(r => r.StartUtc).Should().Equal(new[] { At(5), At(5, 12), At(5, 24) }, (a, b) => Math.Abs((a - b).TotalMilliseconds) <= 250);
        rows.Select(r => r.DurationMs).Should().Equal(new[] { 12_000, 12_000, 6_000 }, (a, b) => Math.Abs(a - b) <= 250);
    }

    [Fact]
    public async Task Download_session_sends_a_keepalive_before_each_file()
    {
        await Seed((At(0), 300_000), (At(10), 1_200_000));
        foreach (var m in new[] { 5, 7 })
        {
            var begin = new DateTime(2026, 7, 1, 15, m, 0);
            _fake.Files.Add((new XmFileEntry($"/idea0/2026-07-01/001/{begin:HH.mm.ss}.h264", begin, begin.AddSeconds(30), 0, 1), []));   // no video
        }

        await _sd.RunOnceAsync("c1", CancellationToken.None);

        _fake.Downloads.Should().HaveCount(2);
        _fake.KeepAlives.Should().Be(2);
    }

    [Fact]
    public async Task Offline_to_online_marks_camera_pending()
    {
        await _sd.StartAsync(CancellationToken.None);
        try
        {
            var store = _factory.Services.GetRequiredService<StreamStateStore>();
            var state = store.GetOrAdd(new StreamKey("c1", "ptz"));
            store.SetStatus(state, StreamStatus.Offline, _clock.GetUtcNow());
            _sd.Pending.Should().BeEmpty();

            store.SetStatus(state, StreamStatus.Online, _clock.GetUtcNow());

            _sd.Pending.Should().Contain("c1");   // the agent is not connected, so it stays pending
        }
        finally { await _sd.StopAsync(CancellationToken.None); }
    }
}
