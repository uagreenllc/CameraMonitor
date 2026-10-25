namespace CameraMonitor.Api.Features.Streams;

public interface IFfmpegProcess : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    DateTimeOffset? LastProgressAt { get; }
    ProgressState Progress { get; }
    Task<int> Exited { get; }
    void Kill();
}

public delegate IFfmpegProcess FfmpegFactory(IReadOnlyList<string> args, string logName);
