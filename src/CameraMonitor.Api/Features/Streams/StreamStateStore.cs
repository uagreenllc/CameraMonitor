using System.Collections.Concurrent;

namespace CameraMonitor.Api.Features.Streams;

public sealed class StreamStateStore
{
    private readonly ConcurrentDictionary<StreamKey, StreamState> _states = new();

    /// <summary>Raised by workers when the status actually changes; the second argument is the previous status.</summary>
    public event Action<StreamState, StreamStatus>? StatusChanged;

    public StreamState GetOrAdd(StreamKey key) => _states.GetOrAdd(key, k => new StreamState { Key = k });

    public bool TryGet(StreamKey key, out StreamState state) => _states.TryGetValue(key, out state!);

    public bool Remove(StreamKey key) => _states.TryRemove(key, out _);

    public IReadOnlyList<StreamState> All() => [.. _states.Values];

    public void SetStatus(StreamState state, StreamStatus status, DateTimeOffset now)
    {
        var previous = state.Status;
        if (previous == status) return;
        state.Status = status;
        if (status == StreamStatus.Online) state.OnlineSince = now;
        else if (status == StreamStatus.Offline) state.OfflineSince = now;
        StatusChanged?.Invoke(state, previous);
    }
}
