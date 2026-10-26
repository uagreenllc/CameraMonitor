namespace CameraMonitor.Api.Infrastructure;

/// <summary>
/// Write-only wrapper that holds writes to at most <paramref name="kbps"/> kbit/s on average (0 = unlimited),
/// waiting on <paramref name="clock"/>. Async writes only: the downloads that use it write asynchronously.
/// </summary>
public sealed class ThrottledStream(Stream inner, int kbps, TimeProvider clock) : Stream
{
    private static readonly TimeSpan MaxBurst = TimeSpan.FromSeconds(1);
    private readonly double _bytesPerSecond = kbps * 1000 / 8.0;
    private readonly long _start = clock.GetTimestamp();
    private double _sent;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use WriteAsync.");

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await inner.WriteAsync(buffer, ct);
        if (kbps <= 0)
        {
            return;
        }

        // Token bucket: credit accrues with time, at most MaxBurst of it is saved up across a pause.
        var elapsed = clock.GetElapsedTime(_start).TotalSeconds;
        _sent = Math.Max(_sent, (elapsed - MaxBurst.TotalSeconds) * _bytesPerSecond) + buffer.Length;
        var due = (_sent / _bytesPerSecond) - elapsed;
        if (due > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(due), clock, ct);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
}
