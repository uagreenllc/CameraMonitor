using System.Diagnostics;
using CameraMonitor.Api.Features.Backfill;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Infrastructure.Xm;
using CameraMonitor.Tests.Xm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Tests.Backfill;

[Collection("real-ffmpeg")]
public class SdRemuxerTests
{
    [Fact]
    public void ParseSegmentList_reads_csv()
    {
        var list = SdRemuxer.ParseSegmentList(
            "00000.ts,0.000000,10.000000\n00001.ts,10.000000,20.000000\n00002.ts,20.000000,25.000000\n", "out");
        list.Should().HaveCount(3);
        list[0].Path.Should().Be(Path.Combine("out", "00000.ts"));
        list[2].Start.Should().Be(TimeSpan.FromSeconds(20));
        list[2].End.Should().Be(TimeSpan.FromSeconds(25));
    }

    private static string Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0);
        return o;
    }

    [FfmpegFact]
    public async Task Remux_cuts_annexb_into_10s_segments()
    {
        var dir = $"./cm-tests-data/remux-{Guid.NewGuid():N}";
        Directory.CreateDirectory(dir);
        try
        {
            var raw = $"{dir}/raw.264";
            Run("ffmpeg", $"-nostdin -loglevel error -f lavfi -i testsrc2=size=320x240:rate=15 -t 25 -c:v libx264 -preset veryfast -g 15 -bf 0 -pix_fmt yuv420p -f h264 {raw}");

            var parser = new XmFrameParser();
            var inPath = $"{dir}/in.264";
            await using (var o = File.Create(inPath))
                parser.Feed(XmTestStream.IFrame(new DateTime(2026, 10, 26, 14, 0, 0), 15, 320, 240, await File.ReadAllBytesAsync(raw)), o);

            var outDir = $"{dir}/out";
            Directory.CreateDirectory(outDir);
            var remuxer = new SdRemuxer(Options.Create(new ServerOptions()), NullLogger<SdRemuxer>.Instance);
            var segs = await remuxer.RemuxAsync(inPath, 15, outDir, default);

            segs.Should().HaveCount(3);
            var expected = new[] { (0, 10), (10, 20), (20, 25) };
            for (var i = 0; i < 3; i++)
            {
                segs[i].Start.TotalSeconds.Should().BeApproximately(expected[i].Item1, 0.2);
                segs[i].End.TotalSeconds.Should().BeApproximately(expected[i].Item2, 0.2);
                new FileInfo(segs[i].Path).Length.Should().BeGreaterThan(0);
            }
            var dur = Run("ffprobe", $"-v error -show_entries format=duration -of csv=p=0 {segs[0].Path}");
            double.Parse(dur.Trim(), System.Globalization.CultureInfo.InvariantCulture).Should().BeApproximately(10, 0.5);
        }
        finally { Directory.Delete(dir, true); }
    }

    [FfmpegFact]
    public async Task Failed_remux_reports_ffmpeg_stderr()
    {
        var dir = $"./cm-tests-data/remux-{Guid.NewGuid():N}";
        Directory.CreateDirectory(dir);
        try
        {
            var remuxer = new SdRemuxer(Options.Create(new ServerOptions()), NullLogger<SdRemuxer>.Instance);

            var act = () => remuxer.RemuxAsync($"{dir}/cm-missing.264", 15, dir, default);

            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("cm-missing.264");
        }
        finally { Directory.Delete(dir, true); }
    }
}
