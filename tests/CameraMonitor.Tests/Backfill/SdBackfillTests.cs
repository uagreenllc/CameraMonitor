using System.Diagnostics;
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
            .WithSetting("Server:TimeZone", "Europe/Kyiv");
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

    /// <summary>The 25-s 15-fps test stream of Task 8, wrapped as one Xiongmai I-frame stamped with <paramref name="iFrameTime"/>.</summary>
    private void AddSdFile(DateTime iFrameTime)
    {
        var dir = $"./cm-tests-data/sdsrc-{Guid.NewGuid():N}";
        Directory.CreateDirectory(dir);
        try
        {
            var raw = $"{dir}/raw.264";
            Run("ffmpeg", $"-nostdin -loglevel error -f lavfi -i testsrc2=size=320x240:rate=15 -t 25 -c:v libx264 -preset veryfast -g 15 -bf 0 -pix_fmt yuv420p -f h264 {raw}");
            var bytes = XmTestStream.IFrame(iFrameTime, 15, 320, 240, File.ReadAllBytes(raw));
            _fake.Files.Add((new XmFileEntry("/idea0/2026-07-01/001/15.05.00-15.05.25[R][@1][0].h264",
                new DateTime(2026, 7, 1, 15, 5, 0), new DateTime(2026, 7, 1, 15, 5, 25), bytes.Length / 1024, 1), bytes));
        }
        finally { Directory.Delete(dir, true); }
    }

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
