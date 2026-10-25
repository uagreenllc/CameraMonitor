namespace CameraMonitor.Api.Infrastructure.Db;

public sealed class SegmentRow
{
    public long Id { get; set; }
    public required string CameraId { get; set; }
    public required string LensId { get; set; }
    public DateTime StartUtc { get; set; }
    public int DurationMs { get; set; }

    /// <summary>Relative to DataDir, forward slashes: rec/{cam}/{lens}/yyyyMMdd/HH/HHmmss.ts. Unique.</summary>
    public required string Path { get; set; }

    public long Bytes { get; set; }
    public bool Discontinuity { get; set; }
}
