using CameraMonitor.Api.Features.Cameras;
using FluentAssertions;

namespace CameraMonitor.Tests.Cameras;

public class CamerasOptionsValidatorTests
{
    private static LensConfig Lens(string id) => new() { Id = id };

    private static CameraConfig Cam(string id, string? password = null, string vendor = "xm", params string[] lenses) => new()
    {
        Id = id,
        Name = id,
        Host = "h",
        Password = password,
        Vendor = vendor,
        Lenses = (lenses.Length == 0 ? ["fixed", "ptz"] : lenses).Select(Lens).ToList(),
    };

    private static Microsoft.Extensions.Options.ValidateOptionsResult Run(params CameraConfig[] cams) =>
        new CamerasOptionsValidator().Validate(null, new CamerasOptions { Items = [.. cams] });

    [Fact]
    public void Valid_config_passes() => Run(Cam("a", "pw"), Cam("b", "pw")).Succeeded.Should().BeTrue();

    [Fact]
    public void Duplicate_camera_id_fails()
    {
        var r = Run(Cam("dup", "pw"), Cam("dup", "pw"));
        r.Failed.Should().BeTrue();
        r.FailureMessage.Should().Contain("dup");
    }

    [Fact]
    public void Duplicate_lens_id_within_camera_fails() =>
        Run(Cam("a", "pw", "xm", "l", "l")).Failed.Should().BeTrue();

    [Fact]
    public void Camera_without_lenses_fails() =>
        Run(new CameraConfig { Id = "a", Host = "h", Password = "pw" }).Failed.Should().BeTrue();

    [Fact]
    public void Bad_id_format_fails() => Run(Cam("Цех 1", "pw")).Failed.Should().BeTrue();

    [Fact]
    public void Xm_password_with_url_delimiters_fails()
    {
        var r = Run(Cam("cex-gate", "a&b"));
        r.Failed.Should().BeTrue();
        r.FailureMessage.Should().Contain("cex-gate").And.Contain("& ? # / : @");
    }

    [Fact]
    public void Rtsp_vendor_requires_lens_url_fails() =>
        Run(Cam("a", null, "rtsp")).Failed.Should().BeTrue();

    [Fact]
    public void Unknown_vendor_fails() => Run(Cam("a", "pw", "dahua")).Failed.Should().BeTrue();

    [Fact]
    public void Empty_host_fails()
    {
        var cam = Cam("a", "pw");
        cam.Host = "";
        Run(cam).FailureMessage.Should().Contain("a: host is required");
    }

    [Fact]
    public void Xm_channel_below_one_fails()
    {
        var cam = Cam("a", "pw");
        cam.Lenses[0].Channel = 0;
        Run(cam).FailureMessage.Should().Contain("a/fixed: channel must be >= 1");
    }
}
