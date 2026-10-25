using CameraMonitor.Api.Features.Cameras;
using System.Diagnostics;

namespace CameraMonitor.Api.Features.Streams;

public sealed class FfmpegProcess(
    string ffmpegPath, IReadOnlyList<string> args, string logName, TimeProvider clock, ILogger logger) : IFfmpegProcess
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;

    /// <summary>ffmpeg echoes the input URL (with camera credentials) in its messages.</summary>
    public static string RedactLine(string line) => RtspUrlBuilder.Redact(line);

    public DateTimeOffset? LastProgressAt { get; private set; }
    public ProgressState Progress { get; } = new();
    public Task<int> Exited => _exited.Task;

    public Task StartAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["TZ"] = "UTC";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start(); // Win32Exception propagates when the binary is missing
        _process = process;

        var stdout = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
                if (ProgressParser.TryApply(line, Progress)) LastProgressAt = clock.GetUtcNow();
        });
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                logger.LogWarning("[{Name}] {Line}", logName, RedactLine(line));
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(stdout, stderr);
                await process.WaitForExitAsync();
                _exited.TrySetResult(process.ExitCode);
            }
            catch (Exception ex)
            {
                _exited.TrySetException(ex);
            }
        });
        return Task.CompletedTask;
    }

    public void Kill()
    {
        try { _process?.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null) return;
        Kill();
        try { await _exited.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* best effort */ }
        _process.Dispose();
    }
}
