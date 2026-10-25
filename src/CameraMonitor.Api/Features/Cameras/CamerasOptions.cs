namespace CameraMonitor.Api.Features.Cameras;

public sealed class CamerasOptions
{
    public const string Section = "Cameras";
    public string? DefaultPassword { get; set; }
    public string? DefaultPasswordFile { get; set; }
    public List<CameraConfig> Items { get; set; } = [];
}

public sealed class CameraConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string User { get; set; } = "admin";
    public string? Password { get; set; }
    public string? PasswordFile { get; set; }

    /// <summary>"xm" or "rtsp".</summary>
    public string Vendor { get; set; } = "xm";
    public List<LensConfig> Lenses { get; set; } = [];
}

public sealed class LensConfig
{
    public string Id { get; set; } = "";
    public int Channel { get; set; } = 1;
    public bool Ptz { get; set; }
    public string? RtspUrl { get; set; }
}
