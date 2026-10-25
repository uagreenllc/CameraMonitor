using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Archive;

public sealed class ExportService(IOptions<ServerOptions> server)
{
    public static readonly TimeSpan MaxRange = TimeSpan.FromHours(2);

    public async Task<int> WriteMp4Async(IReadOnlyList<string> absoluteSegmentPaths, Stream output, CancellationToken ct, TimeSpan? timeout = null)
    {
        var tmp = Path.GetTempFileName();
        var list = Path.ChangeExtension(tmp, ".txt");
        File.Move(tmp, list);
        try
        {
            await File.WriteAllLinesAsync(list,
                absoluteSegmentPaths.Select(p => $"file '{p.Replace('\\', '/').Replace("'", "'\\''")}'"), ct);
            // ponytail: concat across discontinuities may stutter at gap boundaries; re-mux per range if it matters
            string[] args = ["-nostdin", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", list,
                "-c", "copy", "-f", "mp4", "-movflags", "frag_keyframe+empty_moov", "pipe:1"];
            return await ProcessRunner.RunAsync(server.Value.FfmpegPath, args, output, timeout ?? TimeSpan.FromMinutes(10), ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return -2; // internal timeout, not a client disconnect: the caller must treat it as a failure
        }
        finally
        {
            File.Delete(list);
        }
    }
}
