using System.Collections.Concurrent;

namespace CameraMonitor.Api.Features.Backfill;

public sealed class CameraBackfill
{
    public required string CameraId { get; init; }
    public int Queued { get; set; }

    /// <summary>"ptz 14:03–14:20" (camera-local time) while a gap is being filled.</summary>
    public string? Current { get; set; }

    public int FilesDone { get; set; }
    public int SegmentsAdded { get; set; }
    public int GapsNoFiles { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastRunUtc { get; set; }
}

public sealed class BackfillStatus
{
    private readonly ConcurrentDictionary<string, CameraBackfill> _cameras = new();

    public CameraBackfill GetOrAdd(string cameraId) => _cameras.GetOrAdd(cameraId, id => new CameraBackfill { CameraId = id });

    public IReadOnlyList<CameraBackfill> All() => [.. _cameras.Values.OrderBy(c => c.CameraId)];
}
