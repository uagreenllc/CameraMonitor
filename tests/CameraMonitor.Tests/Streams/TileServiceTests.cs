using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Streams;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Tests.Streams;

public class TileServiceTests
{
    [Fact]
    public void PickLatestFinishedSegment_returns_second_newest()
    {
        TileService.PickLatestFinishedSegment(["1.ts", "2.ts", "3.ts"]).Should().Be("2.ts");
        TileService.PickLatestFinishedSegment(["1.ts"]).Should().BeNull();
        TileService.PickLatestFinishedSegment([]).Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_returns_null_when_live_dir_empty()
    {
        var dir = $"./cm-tests-data/tile-{Guid.NewGuid():N}";
        Directory.CreateDirectory(dir);
        try
        {
            var store = new StreamStateStore();
            var key = new StreamKey("a", "b");
            store.GetOrAdd(key).LiveDir = dir;
            var svc = new TileService(store, Options.Create(new ServerOptions()), TimeProvider.System, NullLogger<TileService>.Instance);

            (await svc.GetAsync(key, default)).Should().BeNull();
            (await svc.GetAsync(new StreamKey("x", "y"), default)).Should().BeNull();
        }
        finally { Directory.Delete(dir, true); }
    }
}

[Collection("real-ffmpeg")]
public class TileServiceGrabTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string MakeSegments(int count)
    {
        var dir = $"./cm-tests-data/tile-{Guid.NewGuid():N}";
        Directory.CreateDirectory(dir);
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(dir, $"index{i}.ts");
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg",
                $"-nostdin -loglevel error -f lavfi -i testsrc=d=1:s=64x64:r=5 -f mpegts {path}")
            { CreateNoWindow = true })!;
            p.WaitForExit();
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(i - count));
        }
        return dir;
    }

    private static (TileService Svc, ServerOptions Opts, ManualClock Clock, StreamKey Key) Make(string dir)
    {
        var store = new StreamStateStore();
        var key = new StreamKey("a", "b");
        store.GetOrAdd(key).LiveDir = dir;
        var opts = new ServerOptions();
        var clock = new ManualClock();
        return (new TileService(store, Options.Create(opts), clock, NullLogger<TileService>.Instance), opts, clock, key);
    }

    [FfmpegFact]
    public async Task Concurrent_gets_share_one_grab()
    {
        var dir = MakeSegments(2);
        try
        {
            var (svc, _, _, key) = Make(dir);
            var results = await Task.WhenAll(svc.GetAsync(key, default), svc.GetAsync(key, default), svc.GetAsync(key, default));
            results.Should().OnlyContain(r => r != null && r.Length > 0);
            svc.GrabCount.Should().Be(1);
        }
        finally { Directory.Delete(dir, true); }
    }

    [FfmpegFact]
    public async Task Grab_failure_returns_stale_tile_for_30s_then_null()
    {
        var dir = MakeSegments(2);
        try
        {
            var (svc, opts, clock, key) = Make(dir);
            (await svc.GetAsync(key, default)).Should().NotBeNull();

            opts.FfmpegPath = "no-such-ffmpeg-exe";
            clock.Now += TimeSpan.FromSeconds(5);
            (await svc.GetAsync(key, default)).Should().NotBeNull();

            clock.Now += TimeSpan.FromSeconds(30);
            (await svc.GetAsync(key, default)).Should().BeNull();
        }
        finally { Directory.Delete(dir, true); }
    }

    [FfmpegFact]
    public async Task No_segments_after_cached_tile_returns_null()
    {
        var dir = MakeSegments(2);
        try
        {
            var (svc, _, clock, key) = Make(dir);
            (await svc.GetAsync(key, default)).Should().NotBeNull();

            foreach (var f in Directory.GetFiles(dir, "*.ts")) File.Delete(f);
            clock.Now += TimeSpan.FromSeconds(5);
            (await svc.GetAsync(key, default)).Should().BeNull();
        }
        finally { Directory.Delete(dir, true); }
    }
}
