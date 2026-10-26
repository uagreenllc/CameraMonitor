using System.Globalization;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Streams;
using CameraMonitor.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Backfill;

public sealed class SdRemuxer(IOptions<ServerOptions> server, ILogger<SdRemuxer> logger)
{
    public sealed record RemuxedSegment(string Path, TimeSpan Start, TimeSpan End);

    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public async Task<IReadOnlyList<RemuxedSegment>> RemuxAsync(string annexBPath, int fps, string outDir, CancellationToken ct)
    {
        var o = server.Value;
        var dir = FfmpegArgs.NormalizePath(outDir);
        var listPath = $"{dir}/segments.csv";
        string[] args =
        [
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-fflags", "+genpts", "-r", fps.ToString(CultureInfo.InvariantCulture), "-f", "h264", "-i", FfmpegArgs.NormalizePath(annexBPath),
            "-c:v", "copy", "-an",
            "-f", "segment", "-segment_time", o.SegmentSeconds.ToString(CultureInfo.InvariantCulture), "-segment_format", "mpegts",
            "-segment_list", listPath, "-segment_list_type", "csv", $"{dir}/%05d.ts",
        ];
        var (code, _) = await ProcessRunner.RunAsync(o.FfmpegPath, args, Timeout, ct);
        if (code != 0)
        {
            logger.LogWarning("ffmpeg remux of {Input} failed with exit code {Code}", annexBPath, code);
            throw new InvalidOperationException($"ffmpeg remux failed with exit code {code}");
        }
        return ParseSegmentList(await File.ReadAllTextAsync(listPath, ct), outDir);
    }

    public static IReadOnlyList<RemuxedSegment> ParseSegmentList(string csv, string outDir)
    {
        var result = new List<RemuxedSegment>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split(',');
            result.Add(new RemuxedSegment(Path.Combine(outDir, p[0]),
                TimeSpan.FromSeconds(double.Parse(p[1], CultureInfo.InvariantCulture)),
                TimeSpan.FromSeconds(double.Parse(p[2], CultureInfo.InvariantCulture))));
        }
        return result;
    }
}
