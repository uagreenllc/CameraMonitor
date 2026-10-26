namespace CameraMonitor.Api.Features.Archive;

public sealed class RetentionStatus
{
    public DateTime? OldestSegmentUtc { get; set; }

    /// <summary>End of the newest segment retention ever deleted (this process): the SD backfill must not refill up to here.</summary>
    public DateTime? EvictedThroughUtc { get; set; }
    public long FreeBytes { get; set; }
    public long ArchiveBytes { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public int LastDeleted { get; set; }
}
