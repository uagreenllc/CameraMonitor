namespace CameraMonitor.Api.Infrastructure.Db;

public sealed class EventRow
{
    public long Id { get; set; }
    public required string CameraId { get; set; }
    public required string LensId { get; set; }

    /// <summary>"human" | "motion" | other lower-cased camera event names.</summary>
    public required string Type { get; set; }

    public DateTime StartedUtc { get; set; }

    /// <summary>Null while the alarm is open.</summary>
    public DateTime? EndedUtc { get; set; }

    /// <summary>Relative to DataDir: events/{cam}/{yyyyMMdd}/{HHmmss}-{lens}-{type}.jpg.</summary>
    public string? SnapshotPath { get; set; }
}
