namespace CameraMonitor.Api.Features.Archive;

public interface IDurationProbe
{
    /// <summary>Media duration of the file, or null when it cannot be determined.</summary>
    Task<TimeSpan?> ProbeAsync(string absolutePath, CancellationToken ct);
}
