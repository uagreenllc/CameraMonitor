using System.Net;
using System.Text.Json;
using CameraMonitor.Api.Features.Archive;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Infrastructure.Db;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

    private static async Task SeedRowAsync(ApiFactory f, string path)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Segments.Add(new SegmentRow { CameraId = "a", LensId = "b", StartUtc = T, DurationMs = 10000, Bytes = 1, Path = path });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Export_aborts_when_ffmpeg_fails_after_writing_bytes()
    {
        var dir = Path.GetFullPath($"cm-tests-script-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string script;
            if (OperatingSystem.IsWindows())
            {
                script = Path.Combine(dir, "fake.cmd");
                File.WriteAllText(script, string.Join(Environment.NewLine, "@echo off", "echo partial", "exit /b 1"));
            }
            else
            {
                script = Path.Combine(dir, "fake.sh");
                File.WriteAllText(script, string.Join(Environment.NewLine, "#!/bin/sh", "printf partial", "exit 1"));
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            using var f = new ApiFactory().WithSetting("Server:FfmpegPath", script);
            var c = f.CreateClient();
            Directory.CreateDirectory($"{f.DataDir}/rec/a/b/20261025/14");
            File.WriteAllBytes($"{f.DataDir}/rec/a/b/20261025/14/140000.ts", [1]);
            await SeedRowAsync(f, "rec/a/b/20261025/14/140000.ts");

            var read = async () =>
            {
                using var r = await c.GetAsync($"/api/export?cam=a&lens=b&from={Iso(T)}&to={Iso(T.AddSeconds(10))}", HttpCompletionOption.ResponseHeadersRead);
                r.StatusCode.Should().Be(HttpStatusCode.OK);
                return await r.Content.ReadAsByteArrayAsync();
            };
            // the 200 is already committed with partial bytes; the client must see an aborted body, never a "complete" one
            await read.Should().ThrowAsync<Exception>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteMp4Async_timeout_is_a_failure_code()
    {
        var svc = new ExportService(Options.Create(new ServerOptions()));
        var code = await svc.WriteMp4Async(["x.ts"], Stream.Null, CancellationToken.None, TimeSpan.Zero);
        code.Should().NotBe(0);
    }

    [Fact]
    public async Task Export_ignores_paths_outside_rec_root()
    {
        using var f = new ApiFactory();
        var c = f.CreateClient();
        var name = $"outside-{Guid.NewGuid():N}.ts";
        Directory.CreateDirectory(f.DataDir);
        var outside = Path.GetFullPath(Path.Combine(f.DataDir, "..", name));
        File.WriteAllBytes(outside, [1]);
        try
        {
            await SeedRowAsync(f, "../" + name);
            var r = await c.GetAsync($"/api/export?cam=a&lens=b&from={Iso(T)}&to={Iso(T.AddSeconds(10))}");
            r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            File.Delete(outside);
        }
    }
}
