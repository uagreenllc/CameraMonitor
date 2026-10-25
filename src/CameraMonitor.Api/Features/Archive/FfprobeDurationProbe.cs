using System.Globalization;
using System.Text;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Archive;

public sealed class FfprobeDurationProbe(IOptions<ServerOptions> server, ILogger<FfprobeDurationProbe> logger) : IDurationProbe
{
    public async Task<TimeSpan?> ProbeAsync(string absolutePath, CancellationToken ct)
    {
        try
        {
            var (code, stdout) = await ProcessRunner.RunAsync(
                server.Value.FfprobePath,
                ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", absolutePath],
                TimeSpan.FromSeconds(5), ct);
            if (code != 0) return null;
            return double.TryParse(Encoding.UTF8.GetString(stdout).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0
                ? TimeSpan.FromSeconds(s)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "ffprobe failed for {Path}", absolutePath);
            return null;
        }
    }
}
