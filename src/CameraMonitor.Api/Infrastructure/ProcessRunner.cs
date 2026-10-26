using System.Diagnostics;

namespace CameraMonitor.Api.Infrastructure;

public static class ProcessRunner
{
    /// <summary>Runs <paramref name="exe"/>, copies its stdout to <paramref name="stdout"/>, kills the process tree on timeout or cancellation. Returns the exit code.</summary>
    /// <param name="stderr">Receives the process's stderr (e.g. a <see cref="StderrTail"/>); discarded when null.</param>
    public static async Task<int> RunAsync(string exe, IReadOnlyList<string> args, Stream stdout, TimeSpan timeout, CancellationToken ct, Stream? stderr = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Cannot start {exe}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        // a blocked pipe read may not observe the token (Windows): killing ends it with EOF
        using var kill = cts.Token.Register(() => Kill(process));
        var drain = process.StandardError.BaseStream.CopyToAsync(stderr ?? Stream.Null, cts.Token);
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(stdout, cts.Token);
            await drain;
            await process.WaitForExitAsync(cts.Token);
            cts.Token.ThrowIfCancellationRequested();   // killed by the registration: WaitForExitAsync returns without throwing
            return process.ExitCode;
        }
        catch
        {
            // cancellation, timeout, or the sink threw: never leave the process running
            Kill(process);
            try { await drain; } catch { /* observed; the first error wins */ }
            throw;
        }
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
        catch (System.ComponentModel.Win32Exception) { /* exiting */ }
    }

    public static async Task<(int ExitCode, byte[] Stdout)> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var code = await RunAsync(exe, args, ms, timeout, ct);
        return (code, ms.ToArray());
    }
}

/// <summary>Write-only sink keeping the last <paramref name="capacity"/> bytes written, e.g. ffmpeg's stderr for a failure log.</summary>
public sealed class StderrTail(int capacity = 2048) : Stream
{
    private readonly byte[] _buf = new byte[capacity];
    private int _len;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_buf)
        {
            if (buffer.Length >= _buf.Length)
            {
                buffer[^_buf.Length..].CopyTo(_buf);
                _len = _buf.Length;
                return;
            }

            var drop = Math.Max(0, _len + buffer.Length - _buf.Length);
            Buffer.BlockCopy(_buf, drop, _buf, 0, _len - drop);
            _len -= drop;
            buffer.CopyTo(_buf.AsSpan(_len));
            _len += buffer.Length;
        }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    /// <summary>The kept bytes as UTF-8 text, trimmed (a cut multi-byte character becomes U+FFFD).</summary>
    public override string ToString()
    {
        lock (_buf)
        {
            return System.Text.Encoding.UTF8.GetString(_buf, 0, _len).Trim();
        }
    }
}
