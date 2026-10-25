using System.Globalization;

namespace CameraMonitor.Api.Features.Streams;

public sealed class ProgressState
{
    public TimeSpan OutTime { get; set; }
    public long TotalBytes { get; set; }
    public double Fps { get; set; }
    public string? Bitrate { get; set; }
}

public static class ProgressParser
{
    /// <summary>Applies one `-progress` line; returns true when it ends a block (`progress=...`).</summary>
    public static bool TryApply(string line, ProgressState state)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0) return false;
        var key = line.AsSpan(0, eq);
        var value = line[(eq + 1)..];

        if (key is "progress") return true;
        if (key is "out_time_us" or "out_time_ms") // ffmpeg emits microseconds under both keys
        {
            if (long.TryParse(value, CultureInfo.InvariantCulture, out var us))
                state.OutTime = TimeSpan.FromMicroseconds(us);
        }
        else if (key is "total_size")
        {
            if (long.TryParse(value, CultureInfo.InvariantCulture, out var b)) state.TotalBytes = b;
        }
        else if (key is "fps")
        {
            if (double.TryParse(value, CultureInfo.InvariantCulture, out var f)) state.Fps = f;
        }
        else if (key is "bitrate")
        {
            state.Bitrate = value;
        }
        return false;
    }
}
