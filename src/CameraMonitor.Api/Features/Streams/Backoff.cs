namespace CameraMonitor.Api.Features.Streams;

public static class Backoff
{
    /// <summary>2^attempt seconds, attempt clamped to 0..6 (1, 2, 4, 8, 16, 32, 60 cap).</summary>
    public static TimeSpan NextDelay(int attempt)
    {
        var a = Math.Clamp(attempt, 0, 6);
        return a == 6 ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(1 << a);
    }
}
