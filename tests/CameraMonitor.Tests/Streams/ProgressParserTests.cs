using CameraMonitor.Api.Features.Streams;

namespace CameraMonitor.Tests.Streams;

public class ProgressParserTests
{
    [Fact]
    public void Parses_fields_and_signals_block_end()
    {
        var s = new ProgressState();

        ProgressParser.TryApply("out_time_us=12500000", s).Should().BeFalse();
        ProgressParser.TryApply("fps=14.98", s).Should().BeFalse();
        ProgressParser.TryApply("bitrate=2501.3kbits/s", s).Should().BeFalse();
        ProgressParser.TryApply("total_size=1024", s).Should().BeFalse();
        ProgressParser.TryApply("progress=continue", s).Should().BeTrue();

        s.OutTime.Should().Be(TimeSpan.FromSeconds(12.5));
        s.Fps.Should().Be(14.98);
        s.Bitrate.Should().Be("2501.3kbits/s");
        s.TotalBytes.Should().Be(1024);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("fps=abc")]
    [InlineData("out_time_us=N/A")]
    [InlineData("")]
    public void Ignores_garbage(string line)
    {
        var s = new ProgressState();
        ProgressParser.TryApply(line, s).Should().BeFalse();
        s.OutTime.Should().Be(TimeSpan.Zero);
        s.Fps.Should().Be(0);
    }
}
