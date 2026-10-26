using System.Collections.Concurrent;

namespace CameraMonitor.Api.Features.Events;

public sealed class XmAgentState
{
    public required string CameraId { get; init; }
    public bool Connected { get; set; }
    public DateTimeOffset? ConnectedSince { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastAlarmAt { get; set; }

    /// <summary>Camera time − server time at the last sync.</summary>
    public double? ClockDriftSeconds { get; set; }

    public int Reconnects { get; set; }

    /// <summary>MainFormat.Video.GOP after EnsureEncode.</summary>
    public int? Gop { get; set; }
}

public sealed class XmAgentStore
{
    private readonly ConcurrentDictionary<string, XmAgentState> _states = new();
    private readonly ConcurrentDictionary<string, XmCameraAgent> _agents = new();

    /// <summary>Camera id; raised after OnConnected succeeds.</summary>
    public event Action<string>? Connected;

    public XmAgentState GetOrAdd(string cameraId) => _states.GetOrAdd(cameraId, id => new XmAgentState { CameraId = id });

    public bool TryGet(string cameraId, out XmAgentState state) => _states.TryGetValue(cameraId, out state!);

    public IReadOnlyList<XmAgentState> All() => [.. _states.Values];

    public bool Remove(string cameraId) => _states.TryRemove(cameraId, out _);

    public void Register(string cameraId, XmCameraAgent agent) => _agents[cameraId] = agent;

    public XmCameraAgent? Agent(string cameraId) => _agents.GetValueOrDefault(cameraId);

    public void Unregister(string cameraId) => _agents.TryRemove(cameraId, out _);

    public void RaiseConnected(string cameraId) => Connected?.Invoke(cameraId);
}
