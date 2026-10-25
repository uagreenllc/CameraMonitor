using CameraMonitor.Api.Features.Streams;
using Microsoft.Extensions.Logging.Abstractions;

namespace CameraMonitor.Tests.Streams;

public class FfmpegProcessTests
{
    private static FfmpegProcess Create(params string[] args)
        => new("ffmpeg", args, "test/stream", TimeProvider.System, NullLogger.Instance);

    [FfmpegFact]
    public async Task StartAsync_reports_progress_then_exits_zero()
    {
        await using var p = Create("-nostdin", "-f", "lavfi", "-i", "testsrc=duration=1:size=64x64:rate=10",
            "-progress", "pipe:1", "-f", "null", "-");

        await p.StartAsync(CancellationToken.None);

        (await p.Exited.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(0);
        p.LastProgressAt.Should().NotBeNull();
        p.Progress.OutTime.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(0.9));
    }

    [FfmpegFact]
    public async Task Kill_terminates_running_process()
    {
        await using var p = Create("-nostdin", "-re", "-f", "lavfi", "-i", "testsrc=duration=60:size=64x64:rate=10",
            "-progress", "pipe:1", "-f", "null", "-");

        await p.StartAsync(CancellationToken.None);
        for (var i = 0; i < 50 && p.LastProgressAt is null; i++) await Task.Delay(100);
        p.LastProgressAt.Should().NotBeNull();

        p.Kill();

        await p.Exited.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
