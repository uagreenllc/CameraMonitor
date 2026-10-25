using CameraMonitor.Api.Features.Archive;
using FluentAssertions;

namespace CameraMonitor.Tests.Archive;

public sealed class HlsPlaylistParserTests
{
    public const string Fixture = """
        #EXTM3U
        #EXT-X-VERSION:6
        #EXT-X-TARGETDURATION:10
        #EXT-X-MEDIA-SEQUENCE:12
        #EXT-X-PROGRAM-DATE-TIME:2026-10-25T14:03:10.120+0000
        #EXTINF:10.000000,
        20261025/14/140310.ts
        #EXT-X-PROGRAM-DATE-TIME:2026-10-25T14:03:20.120+00:00
        #EXTINF:9.960000,
        20261025/14/140320.ts
        """;

    [Fact]
    public void Parses_both_offset_styles()
    {
        var entries = HlsPlaylistParser.Parse(Fixture);

        entries.Should().HaveCount(2);
        entries[0].ProgramDateTime.Should().Be(new DateTimeOffset(2026, 10, 25, 14, 3, 10, 120, TimeSpan.Zero));
        entries[0].DurationSeconds.Should().Be(10.0);
        entries[0].FileName.Should().Be("20261025/14/140310.ts");
        entries[1].ProgramDateTime.Should().Be(new DateTimeOffset(2026, 10, 25, 14, 3, 20, 120, TimeSpan.Zero));
        entries[1].DurationSeconds.Should().Be(9.96);
    }

    [Fact]
    public void Entry_without_pdt_is_skipped()
    {
        var text = "#EXTM3U\n#EXTINF:10.0,\na.ts\n#EXT-X-PROGRAM-DATE-TIME:2026-10-25T14:03:20.000Z\n#EXTINF:10.0,\nb.ts\n";

        HlsPlaylistParser.Parse(text).Select(e => e.FileName).Should().Equal("b.ts");
    }

    [Fact]
    public void Z_offset_parses()
    {
        var text = "#EXT-X-PROGRAM-DATE-TIME:2026-10-25T14:03:20Z\n#EXTINF:10.0,\nb.ts\n";

        HlsPlaylistParser.Parse(text).Single().ProgramDateTime.Should().Be(new DateTimeOffset(2026, 10, 25, 14, 3, 20, TimeSpan.Zero));
    }
}
