using System.Globalization;
using System.Text.RegularExpressions;

namespace CameraMonitor.Api.Features.Archive;

public sealed record PlaylistEntry(DateTimeOffset ProgramDateTime, double DurationSeconds, string FileName);

public static partial class HlsPlaylistParser
{
    private const string Pdt = "#EXT-X-PROGRAM-DATE-TIME:";
    private const string Inf = "#EXTINF:";

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$")]
    private static partial Regex CompactOffset();

    public static IReadOnlyList<PlaylistEntry> Parse(string m3u8)
    {
        var entries = new List<PlaylistEntry>();
        DateTimeOffset? pdt = null;
        double? duration = null;
        foreach (var raw in m3u8.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(Pdt, StringComparison.Ordinal))
            {
                var text = CompactOffset().Replace(line[Pdt.Length..], "$1:$2");
                pdt = DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var p) ? p : null;
            }
            else if (line.StartsWith(Inf, StringComparison.Ordinal))
            {
                var value = line[Inf.Length..].Split(',')[0];
                duration = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            }
            else if (line.Length > 0 && line[0] != '#')
            {
                if (pdt is { } t && duration is { } d) entries.Add(new PlaylistEntry(t, d, line));
                pdt = null;
                duration = null;
            }
        }
        return entries;
    }
}
