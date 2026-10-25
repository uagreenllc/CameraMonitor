using System.Collections.Concurrent;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Streams;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CameraMonitor.Tests.Streams;

public class StreamSupervisorTests
{
    [Fact]
    public void BuildSpecs_builds_one_spec_per_lens_with_dirs()
    {
        var cams = new CamerasOptions
        {
            DefaultPassword = "pw",
            Items =
            [
                new CameraConfig { Id = "cex-gate", Name = "Gate", Host = "10.0.0.1", Lenses = [new() { Id = "ptz", Channel = 1, Ptz = true }, new() { Id = "wide", Channel = 2 }] },
                new CameraConfig { Id = "yard", Name = "Yard", Host = "10.0.0.2", Lenses = [new() { Id = "main", Channel = 1 }, new() { Id = "aux", Channel = 2 }] },
            ],
        };
        var server = new ServerOptions { DataDir = "/data", LiveDir = "/run/live" };

        var specs = StreamSupervisor.BuildSpecs(cams, server);

        specs.Should().HaveCount(4);
        var ptz = specs.Single(s => s.Key == new StreamKey("cex-gate", "ptz"));
        ptz.RecDir.Should().Be("/data/rec/cex-gate/ptz");
        ptz.LiveDir.Should().Be("/run/live/cex-gate/ptz");
        ptz.RtspUrl.Should().Be(RtspUrlBuilder.Build(cams.Items[0], cams.Items[0].Lenses[0], "pw"));
    }

    private static CameraConfig Cam(string id, string host) =>
        new() { Id = id, Name = id, Host = host, Lenses = [new() { Id = "main", Channel = 1 }] };

    private static async Task Until(Func<bool> cond)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(1);
        cond().Should().BeTrue("the condition should hold within 2 s");
    }

    [Fact]
    public async Task Config_change_stops_removed_and_starts_added_streams()
    {
        var dir = Path.Combine("cm-tests", Guid.NewGuid().ToString("N")); // relative: no drive colon
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new StreamStateStore();
        var procs = new ConcurrentDictionary<string, FakeFfmpegProcess>();
        FfmpegFactory ffmpeg = (_, name) => procs[name] = new FakeFfmpegProcess(clock);
        var monitor = Substitute.For<IOptionsMonitor<CamerasOptions>>();
        monitor.CurrentValue.Returns(new CamerasOptions { Items = [Cam("a", "10.0.0.1"), Cam("b", "10.0.0.2")] });
        Action<CamerasOptions, string?>? listener = null;
        monitor.OnChange(Arg.Do<Action<CamerasOptions, string?>>(l => listener = l)).Returns(Substitute.For<IDisposable>());
        var server = Options.Create(new ServerOptions { DataDir = $"{dir}/data", LiveDir = $"{dir}/live" });
        var supervisor = new StreamSupervisor(monitor, server, store, ffmpeg, clock, NullLoggerFactory.Instance);
        try
        {
            await supervisor.StartAsync(CancellationToken.None);
            await Until(() => listener is not null && procs.ContainsKey("a/main") && procs.ContainsKey("b/main"));

            monitor.CurrentValue.Returns(new CamerasOptions { Items = [Cam("a", "10.0.0.1"), Cam("c", "10.0.0.3")] });
            listener!(monitor.CurrentValue, null);
            for (var i = 0; i < 20 && !procs.ContainsKey("c/main"); i++) // past the 500 ms debounce
            {
                await Task.Delay(15);
                clock.Advance(TimeSpan.FromMilliseconds(100));
            }

            await Until(() => procs.ContainsKey("c/main"));
            procs["b/main"].KillCalls.Should().BeGreaterThan(0);
            store.TryGet(new StreamKey("b", "main"), out _).Should().BeFalse();
            procs["a/main"].KillCalls.Should().Be(0, "unchanged streams keep running");
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
