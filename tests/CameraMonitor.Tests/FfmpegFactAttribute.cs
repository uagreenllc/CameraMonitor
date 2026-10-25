using System.Diagnostics;

namespace CameraMonitor.Tests;

public sealed class FfmpegFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(Probe);

    public FfmpegFactAttribute()
    {
        if (!Available.Value) Skip = "ffmpeg not available";
    }

    private static bool Probe()
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
