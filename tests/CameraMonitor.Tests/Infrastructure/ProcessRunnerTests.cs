using CameraMonitor.Api.Infrastructure;

namespace CameraMonitor.Tests.Infrastructure;

[Collection("real-ffmpeg")]
public class ProcessRunnerTests
{
    private static readonly string[] Endless = ["-nostdin", "-loglevel", "error", "-re", "-f", "lavfi", "-i", "testsrc=s=64x64:r=25", "-f", "mpegts", "pipe:1"];

    private sealed class ThrowingSink : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            throw new InvalidDataException("corrupt");
    }

    [FfmpegFact]
    public async Task Sink_exception_kills_the_process_and_surfaces()
    {
        var run = ProcessRunner.RunAsync("ffmpeg", Endless, new ThrowingSink(), Timeout.InfiniteTimeSpan, CancellationToken.None);

        await run.Invoking(t => t.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<InvalidDataException>();
    }

    [FfmpegFact]
    public async Task Stderr_tail_keeps_the_last_bytes_of_the_error_output()
    {
        var tail = new StderrTail();
        string[] args = ["-nostdin", "-hide_banner", "-i", "cm-does-not-exist.mp4", "-f", "null", "-"];

        var code = await ProcessRunner.RunAsync("ffmpeg", args, Stream.Null, TimeSpan.FromSeconds(30), CancellationToken.None, tail);

        code.Should().NotBe(0);
        tail.ToString().Should().Contain("cm-does-not-exist.mp4");
    }

    [Fact]
    public void Stderr_tail_is_bounded()
    {
        var tail = new StderrTail(8);
        tail.Write("0123456789"u8);
        tail.Write("ab"u8);

        tail.ToString().Should().Be("456789ab");
    }

    [FfmpegFact]
    public async Task Cancellation_with_infinite_timeout_kills_the_process()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var run = ProcessRunner.RunAsync("ffmpeg", Endless, Stream.Null, Timeout.InfiniteTimeSpan, cts.Token);

        await run.Invoking(t => t.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<OperationCanceledException>();
    }
}
