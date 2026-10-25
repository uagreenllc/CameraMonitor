using System.Globalization;
using System.Text;
using CameraMonitor.Api.Infrastructure.Db;

namespace CameraMonitor.Api.Features.Archive;

public static class PlaylistBuilder
{
    public static string BuildVod(IReadOnlyList<SegmentRow> segments, Func<SegmentRow, string> segmentUrl)
    {
        var target = segments.Count == 0 ? 10 : (int)Math.Ceiling(segments.Max(s => s.DurationMs) / 1000.0);
        var sb = new StringBuilder()
            .Append("#EXTM3U\n#EXT-X-VERSION:3\n")
            .Append(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{target}\n")
            .Append("#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n");
        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0 && segments[i].Discontinuity) sb.Append("#EXT-X-DISCONTINUITY\n");
            sb.Append(CultureInfo.InvariantCulture, $"#EXTINF:{segments[i].DurationMs / 1000.0:F3},\n{segmentUrl(segments[i])}\n");
        }
        return sb.Append("#EXT-X-ENDLIST\n").ToString();
    }
}
