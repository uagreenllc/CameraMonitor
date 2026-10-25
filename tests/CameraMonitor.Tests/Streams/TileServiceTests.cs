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
