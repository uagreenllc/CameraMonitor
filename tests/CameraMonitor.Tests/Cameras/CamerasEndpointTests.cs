using System.Text.Json;
using CameraMonitor.Api.Features.Cameras;

namespace CameraMonitor.Tests.Cameras;

public class CamerasEndpointTests
{
    [Fact]
    public async Task Get_cameras_lists_cameras_with_lens_status()
    {
        var cams = new CamerasOptions
        {
            DefaultPassword = "secret",
            Items =
            [
                new CameraConfig { Id = "a", Name = "A", Host = "h1", Lenses = [new() { Id = "main", Channel = 1 }, new() { Id = "ptz", Channel = 2, Ptz = true }] },
                new CameraConfig { Id = "b", Name = "B", Host = "h2", Lenses = [new() { Id = "main", Channel = 1 }] },
            ],
        };
        using var factory = new ApiFactory().WithCameras(cams);

        var resp = await factory.CreateClient().GetAsync("/api/cameras");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain("secret").And.NotContain("rtsp");
        var json = JsonDocument.Parse(body).RootElement;
        json.GetArrayLength().Should().Be(2);
        json[0].GetProperty("id").GetString().Should().Be("a");
        json[0].GetProperty("name").GetString().Should().Be("A");
        var lens = json[0].GetProperty("lenses");
        lens.GetArrayLength().Should().Be(2);
        lens[1].GetProperty("ptz").GetBoolean().Should().BeTrue();
        lens[0].GetProperty("status").GetString().Should().BeOneOf("starting", "online", "offline");
    }

    [Fact]
    public async Task Cameras_report_dvrip_flag()
    {
        var cams = new CamerasOptions
        {
            Items =
            [
                new CameraConfig { Id = "a", Name = "A", Host = "h1", Lenses = [new() { Id = "main", Channel = 1 }] },
                new CameraConfig { Id = "r", Name = "R", Host = "h2", Vendor = "rtsp", Lenses = [new() { Id = "main", RtspUrl = "rtsp://h2/x" }] },
            ],
        };
        using var factory = new ApiFactory().WithCameras(cams);

        var json = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/api/cameras")).RootElement;

        foreach (var cam in json.EnumerateArray())
        {
            cam.GetProperty("dvrip").GetBoolean().Should().BeFalse();
        }
    }
}
