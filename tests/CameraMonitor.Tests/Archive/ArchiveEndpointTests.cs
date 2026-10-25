using System.Net;
using System.Text.Json;
using CameraMonitor.Api.Infrastructure.Db;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CameraMonitor.Tests.Archive;

public sealed class ArchiveEndpointTests
{
    private static readonly DateTime T = new(2026, 10, 25, 14, 0, 0, DateTimeKind.Utc);

    private static async Task SeedAsync(ApiFactory f, int count = 3)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var i = 0; i < count; i++)
        {
            var s = T.AddSeconds(10 * i);
            db.Segments.Add(new SegmentRow
            {
                CameraId = "a",
                LensId = "b",
                StartUtc = s,
                DurationMs = 10000,
                Bytes = 1,
                Path = $"rec/a/b/{s:yyyyMMdd}/{s:HH}/{s:HHmmss}.ts",
            });
        }
        await db.SaveChangesAsync();
    }

    private static string Iso(DateTime d) => Uri.EscapeDataString(d.ToString("O"));

    [Fact]
    public async Task Playlist_returns_overlapping_segments()
    {
        using var f = new ApiFactory();
        var c = f.CreateClient();
        await SeedAsync(f);
        var r = await c.GetAsync($"/archive/a/b.m3u8?from={Iso(T.AddSeconds(5))}&to={Iso(T.AddSeconds(20))}");
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.apple.mpegurl");
        (await r.Content.ReadAsStringAsync()).Split('\n').Count(l => l.EndsWith(".ts")).Should().Be(2);
    }

    [Fact]
    public async Task From_after_to_is_400()
    {
        using var f = new ApiFactory();
        var r = await f.CreateClient().GetAsync($"/archive/a/b.m3u8?from={Iso(T.AddSeconds(5))}&to={Iso(T)}");
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Range_over_24h_is_400()
    {
        using var f = new ApiFactory();
        var r = await f.CreateClient().GetAsync($"/archive/a/b.m3u8?from={Iso(T)}&to={Iso(T.AddHours(25))}");
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Export_over_2h_is_400()
    {
        using var f = new ApiFactory();
        var r = await f.CreateClient().GetAsync($"/api/export?cam=a&lens=b&from={Iso(T)}&to={Iso(T.AddHours(3))}");
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Seg_rejects_traversal()
    {
        using var f = new ApiFactory();
        var r = await f.CreateClient().GetAsync("/seg/a/b/20261025/14/..%2F..%2Fx.ts");
        r.StatusCode.Should().NotBe(HttpStatusCode.OK);
        ((int)r.StatusCode).Should().BeOneOf(400, 404);
    }

    [Fact]
    public async Task Seg_serves_existing_file()
    {
        using var f = new ApiFactory();
        var c = f.CreateClient();
        Directory.CreateDirectory($"{f.DataDir}/rec/a/b/20261025/14");
        File.WriteAllBytes($"{f.DataDir}/rec/a/b/20261025/14/140000.ts", [1, 2, 3]);
        var r = await c.GetAsync("/seg/a/b/20261025/14/140000.ts");
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.MediaType.Should().Be("video/mp2t");
    }

    [Fact]
    public async Task Timeline_merges_ranges()
    {
        using var f = new ApiFactory();
        var c = f.CreateClient();
        await SeedAsync(f);
        using var doc = JsonDocument.Parse(await c.GetStringAsync("/api/timeline?cam=a&lens=b&day=2026-10-25"));
        var ranges = doc.RootElement.GetProperty("ranges");
        ranges.GetArrayLength().Should().Be(1);
        ranges[0].GetProperty("from").GetString().Should().Be("2026-10-25T14:00:00Z");
        ranges[0].GetProperty("to").GetString().Should().Be("2026-10-25T14:00:30Z");
    }
}
