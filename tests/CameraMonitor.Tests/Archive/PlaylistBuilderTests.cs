using CameraMonitor.Api.Features.Archive;
using CameraMonitor.Api.Infrastructure.Db;
using FluentAssertions;

namespace CameraMonitor.Tests.Archive;

public sealed class PlaylistBuilderTests
{
    private static SegmentRow Seg(string file, int ms, bool disc = false) => new()
    {
        CameraId = "a",
        LensId = "b",
        Path = $"rec/a/b/20261025/14/{file}",
        DurationMs = ms,
        Discontinuity = disc,
        StartUtc = new DateTime(2026, 10, 25, 14, 3, 10, DateTimeKind.Utc),
    };

    private static string Url(SegmentRow r) => "/seg/" + r.Path["rec/".Length..];

    [Fact]
    public void BuildVod_matches_expected_text()
    {
        var text = PlaylistBuilder.BuildVod([Seg("140310.ts", 10000), Seg("140330.ts", 10000, true)], Url);
        text.Should().Be("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n"
            + "#EXTINF:10.000,\n/seg/a/b/20261025/14/140310.ts\n#EXT-X-DISCONTINUITY\n#EXTINF:10.000,\n/seg/a/b/20261025/14/140330.ts\n#EXT-X-ENDLIST\n");
    }

    [Fact]
    public void BuildVod_empty_has_header_and_endlist() =>
        PlaylistBuilder.BuildVod([], Url).Should().Be("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-ENDLIST\n");

    [Fact]
    public void TargetDuration_rounds_up() =>
        PlaylistBuilder.BuildVod([Seg("140310.ts", 9960)], Url).Should().Contain("#EXT-X-TARGETDURATION:10\n").And.Contain("#EXTINF:9.960,");
}
