using System.Net;
using CameraMonitor.Api.Features.Streams;
using Microsoft.Extensions.DependencyInjection;

namespace CameraMonitor.Tests.Streams;

public class LiveEndpointTests
{
    private static string SetupLive(ApiFactory factory, string cam = "a", string lens = "b")
    {
        var dir = $"./cm-tests-data/live-{Guid.NewGuid():N}";
        Directory.CreateDirectory(dir);
        factory.Services.GetRequiredService<StreamStateStore>().GetOrAdd(new StreamKey(cam, lens)).LiveDir = dir;
        return dir;
    }

    [Fact]
    public async Task Serves_playlist_with_no_store()
    {
        using var factory = new ApiFactory();
        var dir = SetupLive(factory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "index.m3u8"), "#EXTM3U\n");

            var resp = await factory.CreateClient().GetAsync("/live/a/b/index.m3u8");

            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            resp.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.apple.mpegurl");
            resp.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("/live/a/b/..%2Findex.m3u8")]
    [InlineData("/live/a/..%5Cb/x.ts")]
    public async Task Rejects_traversal(string url)
    {
        using var factory = new ApiFactory();
        var dir = SetupLive(factory);
        try
        {
            var resp = await factory.CreateClient().GetAsync(url);
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Missing_file_404()
    {
        using var factory = new ApiFactory();
        var dir = SetupLive(factory);
        try
        {
            (await factory.CreateClient().GetAsync("/live/a/b/nope.ts")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await factory.CreateClient().GetAsync("/live/x/y/index.m3u8")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { Directory.Delete(dir, true); }
    }
}
