namespace CameraMonitor.Api.Features.Streams;

public readonly record struct StreamKey(string CameraId, string LensId)
{
    public override string ToString() => $"{CameraId}/{LensId}";
}
