using CameraMonitor.Api.Features.Cameras;
using FluentAssertions;

namespace CameraMonitor.Tests.Cameras;

public class RtspUrlBuilderTests
{
    private static readonly CameraConfig Cam = new() { Id = "c", Host = "192.168.50.11", User = "admin" };

    [Fact]
    public void Build_uses_xm_template() =>
        RtspUrlBuilder.Build(Cam, new LensConfig { Id = "l", Channel = 2 }, "s3cret")
            .Should().Be("rtsp://192.168.50.11:554/user=admin&password=s3cret&channel=2&stream=0.sdp?real_stream");

    [Fact]
    public void Build_prefers_lens_override() =>
        RtspUrlBuilder.Build(Cam, new LensConfig { Id = "l", RtspUrl = "rtsp://x/y" }, "s3cret")
            .Should().Be("rtsp://x/y");

    [Fact]
    public void Redact_hides_password_in_both_styles()
    {
        var xm = RtspUrlBuilder.Redact("rtsp://h:554/user=admin&password=s3cret&channel=1&stream=0.sdp?real_stream");
        xm.Should().Contain("password=***").And.NotContain("s3cret");
        RtspUrlBuilder.Redact("rtsp://admin:s3cret@h/x").Should().Be("rtsp://admin:***@h/x");
    }

    [Fact]
    public void Redact_works_on_free_text()
    {
        RtspUrlBuilder.Redact("[rtsp @ 0x1] Error opening input file rtsp://10.0.0.5:554/user=admin&password=s3cret&channel=1&stream=0.sdp?real_stream")
            .Should().Contain("password=***").And.NotContain("s3cret");
        RtspUrlBuilder.Redact("Error opening input file rtsp://admin:s3cret@host/x.")
            .Should().Be("Error opening input file rtsp://admin:***@host/x.");
    }
}
