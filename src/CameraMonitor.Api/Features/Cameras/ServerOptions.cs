namespace CameraMonitor.Api.Features.Cameras;

public sealed class ServerOptions
{
    public const string Section = "Server";
    public string DataDir { get; set; } = "/data";
    public string LiveDir { get; set; } = "/run/live";
    public string CamerasFile { get; set; } = "cameras.json";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public int SegmentSeconds { get; set; } = 10;
    public int HlsSeconds { get; set; } = 2;
    public int RetentionDays { get; set; } = 14;
    public int MinFreeGb { get; set; } = 100;
    public int MinRetentionDays { get; set; } = 3;
    public bool RecordAudio { get; set; }
    /// <summary>"ffmpeg" | "dvrip".</summary>
    public string TileSource { get; set; } = "ffmpeg";
    public string TimeZone { get; set; } = "Europe/Kyiv";
}
