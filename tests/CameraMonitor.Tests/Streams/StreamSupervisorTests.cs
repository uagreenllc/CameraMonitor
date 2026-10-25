using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Streams;

namespace CameraMonitor.Tests.Streams;

public class StreamSupervisorTests
{
    [Fact]
    public void BuildSpecs_builds_one_spec_per_lens_with_dirs()
    {
        var cams = new CamerasOptions
        {
            DefaultPassword = "pw",
            Items =
            [
                new CameraConfig { Id = "cex-gate", Name = "Gate", Host = "10.0.0.1", Lenses = [new() { Id = "ptz", Channel = 1, Ptz = true }, new() { Id = "wide", Channel = 2 }] },
                new CameraConfig { Id = "yard", Name = "Yard", Host = "10.0.0.2", Lenses = [new() { Id = "main", Channel = 1 }, new() { Id = "aux", Channel = 2 }] },
            ],
        };
        var server = new ServerOptions { DataDir = "/data", LiveDir = "/run/live" };

        var specs = StreamSupervisor.BuildSpecs(cams, server);

        specs.Should().HaveCount(4);
        var ptz = specs.Single(s => s.Key == new StreamKey("cex-gate", "ptz"));
        ptz.RecDir.Should().Be("/data/rec/cex-gate/ptz");
        ptz.LiveDir.Should().Be("/run/live/cex-gate/ptz");
        ptz.RtspUrl.Should().Be(RtspUrlBuilder.Build(cams.Items[0], cams.Items[0].Lenses[0], "pw"));
    }
}
