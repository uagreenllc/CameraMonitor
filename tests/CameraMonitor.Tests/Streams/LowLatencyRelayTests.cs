using System.Threading.Channels;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Streams;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using static CameraMonitor.Tests.Streams.Fmp4TestBoxes;

namespace CameraMonitor.Tests.Streams;

/// <summary>Writes the init, then one scripted fragment every 20 ms, then waits for cancellation (or exits early).</summary>
internal sealed class FakeRelaySource(int fragments, int? exitAfter = null)
{
    public static readonly byte[] Init = [.. Box("ftyp", 1, 2), .. Box("moov", 3)];

    // fragment 0 and every 10th is a keyframe
    public static byte[] Fragment(int i) =>
        [.. Box("moof", (byte)i, (byte)(i >> 8)), .. Box("mdat", 0, 0, 0, 2, i % 10 == 0 ? (byte)0x65 : (byte)0x41, (byte)i)];

    private int _started, _cancelled;
    public int Started => Volatile.Read(ref _started);
    public int Cancelled => Volatile.Read(ref _cancelled);
    public IReadOnlyList<string>? Args { get; private set; }

    public async Task<int> Run(IReadOnlyList<string> args, Stream sink, CancellationToken ct)
    {
        Args = args;
        Interlocked.Increment(ref _started);
        try
        {
            await sink.WriteAsync(Init, ct);
            for (var i = 0; i < fragments; i++)
            {
                await Task.Delay(20, ct);
                await sink.WriteAsync(Fragment(i), ct);
                if (exitAfter == i + 1) return 1;
            }

            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelled);
            throw;
        }
    }
}

public class LowLatencyRelayTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private LowLatencyRelay Relay(FakeRelaySource source, int lenses = 1, int maxRelays = 4)
    {
        var monitor = Substitute.For<IOptionsMonitor<CamerasOptions>>();
        monitor.CurrentValue.Returns(new CamerasOptions
        {
            Items = [new CameraConfig
            {
                Id = "c1", Host = "10.0.0.1", Password = "secret",
                Lenses = [.. Enumerable.Range(0, lenses).Select(i => new LensConfig { Id = i == 0 ? "a" : $"a{i}", Channel = i + 1 })],
            }],
        });
        return new LowLatencyRelay(monitor, Options.Create(new ServerOptions { MaxRelays = maxRelays }), source.Run, _clock,
            NullLogger<LowLatencyRelay>.Instance);
    }

    private static readonly StreamKey A = new("c1", "a");

    internal static async Task Until(Func<bool> cond)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
        cond().Should().BeTrue("the condition should hold within 5 s");
    }

    private static async Task<List<byte[]>> Take(ChannelReader<byte[]> reader, int n)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var list = new List<byte[]>();
        while (list.Count < n) list.Add(await reader.ReadAsync(cts.Token));
        return list;
    }

    private static async Task<List<byte[]>> ReadAll(ChannelReader<byte[]> reader)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var list = new List<byte[]>();
        await foreach (var m in reader.ReadAllAsync(cts.Token)) list.Add(m);
        return list;
    }

    [Fact]
    public async Task First_subscriber_starts_source_and_receives_init_then_fragments()
    {
        var source = new FakeRelaySource(3);
        using var relay = Relay(source);

        using var sub = relay.Subscribe(A);

        sub.Should().NotBeNull();
        var msgs = await Take(sub!.Messages, 4);
        msgs[0].Should().Equal(FakeRelaySource.Init);
        msgs.Skip(1).Should().BeEquivalentTo(Enumerable.Range(0, 3).Select(FakeRelaySource.Fragment), o => o.WithStrictOrdering());
        source.Started.Should().Be(1);
        source.Args.Should().Contain("pipe:1");
        relay.ActiveRelays.Should().Be(1);
        relay.ActiveKeys.Should().Equal(A);
    }

    [Fact]
    public async Task Late_subscriber_gets_init_and_gop_backlog_first()
    {
        var source = new FakeRelaySource(3);   // key, p, p, then pause
        using var relay = Relay(source);
        using var first = relay.Subscribe(A)!;
        await Take(first.Messages, 4);

        using var second = relay.Subscribe(A)!;

        var msgs = await Take(second.Messages, 4);
        msgs.Should().BeEquivalentTo(new[] { FakeRelaySource.Init }.Concat(Enumerable.Range(0, 3).Select(FakeRelaySource.Fragment)),
            o => o.WithStrictOrdering());
        source.Started.Should().Be(1);
    }

    [Fact]
    public async Task Source_stops_30s_after_last_unsubscribe_and_resubscribe_cancels_the_timer()
    {
        var source = new FakeRelaySource(1);
        using (var relay = Relay(source))
        {
            relay.Subscribe(A)!.Dispose();
            await Until(() => source.Started == 1);

            _clock.Advance(TimeSpan.FromSeconds(29));
            await Task.Delay(100);
            source.Cancelled.Should().Be(0);
            relay.ActiveRelays.Should().Be(1);

            _clock.Advance(TimeSpan.FromSeconds(2));
            await Until(() => source.Cancelled == 1 && relay.ActiveRelays == 0);
        }

        var source2 = new FakeRelaySource(1);
        using var relay2 = Relay(source2);
        relay2.Subscribe(A)!.Dispose();
        _clock.Advance(TimeSpan.FromSeconds(10));
        using var again = relay2.Subscribe(A)!;
        for (var i = 0; i < 6; i++) _clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        source2.Started.Should().Be(1);
        source2.Cancelled.Should().Be(0);
        relay2.ActiveRelays.Should().Be(1);
    }

    [Fact]
    public void Fifth_relay_is_refused_but_second_viewer_of_an_existing_relay_is_not()
    {
        var source = new FakeRelaySource(0);
        using var relay = Relay(source, lenses: 5);
        var keys = new[] { "a", "a1", "a2", "a3", "a4" }.Select(l => new StreamKey("c1", l)).ToArray();

        var subs = keys.Take(4).Select(relay.Subscribe).ToList();

        subs.Should().OnlyContain(s => s != null);
        relay.Subscribe(keys[4]).Should().BeNull();
        relay.Subscribe(keys[0]).Should().NotBeNull();
        relay.ActiveRelays.Should().Be(4);
    }

    [Fact]
    public async Task Slow_client_is_dropped_not_blocking_others()
    {
        var source = new FakeRelaySource(70);
        using var relay = Relay(source);
        using var slow = relay.Subscribe(A)!;
        using var fast = relay.Subscribe(A)!;
        var received = new List<byte[]>();
        var reading = Task.Run(async () =>
        {
            await foreach (var m in fast.Messages.ReadAllAsync()) lock (received) received.Add(m);
        });

        await Until(() => slow.Messages.Completion.IsCompleted && Count() == 71);

        lock (received)
        {
            received.Skip(1).Should().BeEquivalentTo(Enumerable.Range(0, 70).Select(FakeRelaySource.Fragment), o => o.WithStrictOrdering());
        }

        reading.IsCompleted.Should().BeFalse("the fast client stays subscribed");
        int Count() { lock (received) return received.Count; }
    }

    [Fact]
    public void Unknown_lens_returns_null()
    {
        var source = new FakeRelaySource(0);
        using var relay = Relay(source);

        relay.Subscribe(new StreamKey("c1", "nope")).Should().BeNull();
        relay.Subscribe(new StreamKey("x", "a")).Should().BeNull();
        source.Started.Should().Be(0);
    }

    [Fact]
    public async Task Source_exit_completes_all_clients_and_frees_the_slot()
    {
        var source = new FakeRelaySource(10, exitAfter: 3);
        using var relay = Relay(source);
        using var one = relay.Subscribe(A)!;
        using var two = relay.Subscribe(A)!;

        var a = await ReadAll(one.Messages);
        var b = await ReadAll(two.Messages);

        a.Should().HaveCount(4);
        b.Should().HaveCount(4);
        await Until(() => relay.ActiveRelays == 0);
    }

    [FfmpegFact]
    public async Task Ffmpeg_source_failure_carries_stderr()
    {
        var source = LowLatencyRelay.FfmpegSource("ffmpeg");
        string[] args = ["-nostdin", "-hide_banner", "-loglevel", "error", "-i", "cm-relay-missing.mp4", "-f", "null", "-"];

        var act = () => source(args, Stream.Null, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("cm-relay-missing.mp4");
    }
}
