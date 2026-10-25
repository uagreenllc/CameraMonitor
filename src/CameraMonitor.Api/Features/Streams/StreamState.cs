namespace CameraMonitor.Api.Features.Streams;

public enum StreamStatus { Starting, Online, Offline }

public sealed class StreamState
{
    public required StreamKey Key { get; init; }
    public StreamStatus Status { get; set; } = StreamStatus.Starting;
    public DateTimeOffset? LastProgressAt { get; set; }
    public DateTimeOffset? OnlineSince { get; set; }
    public DateTimeOffset? OfflineSince { get; set; }
    public int Restarts { get; set; }

    /// <summary>Incremented per ffmpeg start.</summary>
    public int Generation { get; set; }
    public double Fps { get; set; }
    public string? Bitrate { get; set; }

    /// <summary>Absolute, normalized.</summary>
    public string RecDir { get; set; } = "";
    public string LiveDir { get; set; } = "";
}
