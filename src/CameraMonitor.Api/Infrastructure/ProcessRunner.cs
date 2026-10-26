using System.Diagnostics;

namespace CameraMonitor.Api.Infrastructure;

public static class ProcessRunner
{
    /// <summary>Runs <paramref name="exe"/>, copies its stdout to <paramref name="stdout"/>, kills the process tree on timeout or cancellation. Returns the exit code.</summary>
    public static async Task<int> RunAsync(string exe, IReadOnlyList<string> args, Stream stdout, TimeSpan timeout, CancellationToken ct)
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
        var drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cts.Token);
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(stdout, cts.Token);
            await drain;
            await process.WaitForExitAsync(cts.Token);
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
