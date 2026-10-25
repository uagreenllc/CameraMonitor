namespace CameraMonitor.Api.Features.Archive;

public sealed class RetentionStatus
{
    public DateTime? OldestSegmentUtc { get; set; }
    public long FreeBytes { get; set; }
    public long ArchiveBytes { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public int LastDeleted { get; set; }
}
