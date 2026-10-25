using CameraMonitor.Api.Features.Streams;

namespace CameraMonitor.Tests.Streams;

public sealed class FakeFfmpegProcess(TimeProvider clock) : IFfmpegProcess
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastProgressTicks;

    public bool Started { get; private set; }
    public int KillCalls { get; private set; }
    public DateTimeOffset? LastProgressAt
    {
        get
        {
            var ticks = Volatile.Read(ref _lastProgressTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public ProgressState Progress { get; } = new();
    public Task<int> Exited => _exited.Task;

    public Task StartAsync(CancellationToken ct)
    {
        Started = true;
        return Task.CompletedTask;
    }

    public void ReportProgress() => Volatile.Write(ref _lastProgressTicks, clock.GetUtcNow().UtcTicks);

    public void Exit(int code) => _exited.TrySetResult(code);

    public void Kill()
    {
        KillCalls++;
        _exited.TrySetResult(-1);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
