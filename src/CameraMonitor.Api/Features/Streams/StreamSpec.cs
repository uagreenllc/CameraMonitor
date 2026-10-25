namespace CameraMonitor.Api.Features.Streams;

/// <summary>RtspUrl contains the camera password: never log it unredacted.</summary>
public sealed record StreamSpec(StreamKey Key, string RtspUrl, string RecDir, string LiveDir);
