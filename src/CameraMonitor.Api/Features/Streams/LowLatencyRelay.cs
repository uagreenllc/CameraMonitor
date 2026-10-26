using System.Threading.Channels;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Streams;

/// <summary>Runs ffmpeg (or a fake) writing fragmented MP4 into <paramref name="sink"/> until <paramref name="ct"/>; returns the exit code.</summary>
public delegate Task<int> RelaySource(IReadOnlyList<string> ffmpegArgs, Stream sink, CancellationToken ct);

/// <summary>One viewer's feed: init segment, GOP backlog, then live fragments. Completes when the relay ends or drops the viewer.</summary>
public sealed class RelaySubscription(ChannelReader<byte[]> messages, Action unsubscribe) : IDisposable
{
    private int _disposed;
    public ChannelReader<byte[]> Messages { get; } = messages;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) unsubscribe();
    }
}

/// <summary>On-demand ffmpeg → fMP4 relay per stream key, shared by all its viewers, stopped 30 s after the last one leaves.</summary>
public sealed class LowLatencyRelay(
    IOptionsMonitor<CamerasOptions> cameras, IOptions<ServerOptions> server, RelaySource source, TimeProvider clock,
    ILogger<LowLatencyRelay> logger) : IDisposable
{
    /// <summary>The real source: ffmpeg until cancelled. A non-zero exit throws with the tail of ffmpeg's stderr, which the relay logs redacted.</summary>
    public static RelaySource FfmpegSource(string ffmpeg) => async (args, sink, ct) =>
    {
        var stderr = new StderrTail();
        var code = await ProcessRunner.RunAsync(ffmpeg, args, sink, Timeout.InfiniteTimeSpan, ct, stderr);
        return code == 0 ? 0 : throw new InvalidOperationException($"ffmpeg exited with code {code}: {stderr}");
    };

    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    public const int ClientQueue = 64;   // messages buffered per client; a client this far behind is dropped
    private const int MaxBacklog = 300;  // ponytail: a GOP longer than this (~60 s) gives late joiners a partial backlog, not unbounded memory

    private readonly RelaySource _source = source;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger _log = logger;

    // Lock order: _relays, then a relay's own lock. Broadcast and unsubscribe take only the relay lock.
    private readonly Dictionary<StreamKey, Relay> _relays = [];

    public int ActiveRelays
    {
        get { lock (_relays) return _relays.Count; }
    }

    public IReadOnlyList<StreamKey> ActiveKeys
    {
        get { lock (_relays) return [.. _relays.Keys]; }
    }

    /// <summary>Null: unknown lens, or <see cref="ServerOptions.MaxRelays"/> relays already run and none for this key.</summary>
    public RelaySubscription? Subscribe(StreamKey key)
    {
        lock (_relays)
        {
            if (!_relays.TryGetValue(key, out var relay))
            {
                var spec = StreamSupervisor.BuildSpecs(cameras.CurrentValue, server.Value).FirstOrDefault(s => s.Key == key);
                if (spec is null || _relays.Count >= server.Value.MaxRelays) return null;
                relay = _relays[key] = new Relay(this, spec);
                relay.Start();
            }

            return relay.Add();
        }
    }

    public void Dispose()
    {
        List<Relay> all;
        lock (_relays)
        {
            all = [.. _relays.Values];
            _relays.Clear();
        }

        foreach (var r in all) r.Stop();
    }

    // ponytail: one lock per relay
    private sealed class Relay(LowLatencyRelay owner, StreamSpec spec)
    {
        private readonly CancellationTokenSource _cts = new();   // never disposed: no timer, no wait handle, so nothing to free
        private readonly List<Channel<byte[]>> _clients = [];
        private readonly List<byte[]> _gop = [];
        private byte[]? _init;
        private CancellationTokenSource? _idle;
        private bool _stopped;

        public void Start() => _ = Task.Run(RunAsync);

        public RelaySubscription Add()
        {
            lock (this)
            {
                _idle?.Cancel();
                _idle = null;
                // the backlog does not count as "behind"
                var ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(ClientQueue + _gop.Count + 1) { FullMode = BoundedChannelFullMode.Wait });
                if (_init is not null)
                {
                    ch.Writer.TryWrite(_init);
                    foreach (var f in _gop) ch.Writer.TryWrite(f);
                }

                _clients.Add(ch);
                return new RelaySubscription(ch.Reader, () => Remove(ch));
            }
        }

        private void Remove(Channel<byte[]> ch)
        {
            lock (this)
            {
                _clients.Remove(ch);
                ch.Writer.TryComplete();
                IdleIfEmpty();
            }
        }

        // caller holds the lock
        private void IdleIfEmpty()
        {
            if (_clients.Count > 0 || _idle is not null || _stopped) return;
            _idle = new CancellationTokenSource();
            _ = IdleStopAsync(_idle.Token);   // Task.Delay registers its timer before this returns
        }

        private async Task IdleStopAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(IdleTimeout, owner._clock, token);
            }
            catch (OperationCanceledException)
            {
                return;   // a viewer came back
            }

            lock (owner._relays)
            {
                lock (this)
                {
                    if (token.IsCancellationRequested || _clients.Count > 0 || _stopped) return;
                    if (owner._relays.GetValueOrDefault(spec.Key) == this) owner._relays.Remove(spec.Key);
                    _stopped = true;
                }
            }

            owner._log.LogInformation("Relay {Stream} idle, stopping", spec.Key);
            Stop();
        }

        public void Stop()
        {
            lock (this) _stopped = true;
            _cts.Cancel();
        }

        private async Task RunAsync()
        {
            var url = RtspUrlBuilder.Redact(spec.RtspUrl);
            try
            {
                owner._log.LogInformation("Relay {Stream} starting from {Url}", spec.Key, url);
                var reader = new Fmp4BoxReader(OnInit, OnFragment);
                var code = await owner._source(FfmpegArgs.LowLatency(spec.RtspUrl), new BoxFeedStream(reader), _cts.Token);
                owner._log.LogWarning("Relay {Stream} source {Url} exited with code {Code}", spec.Key, url, code);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                // idle stop or shutdown
            }
            catch (Exception ex)
            {
                owner._log.LogWarning("Relay {Stream} source {Url} failed: {Error}: {Message}",
                    spec.Key, url, ex.GetType().Name, RtspUrlBuilder.Redact(ex.Message));
            }
            finally
            {
                lock (owner._relays)
                {
                    if (owner._relays.GetValueOrDefault(spec.Key) == this) owner._relays.Remove(spec.Key);
                    lock (this)
                    {
                        _stopped = true;
                        foreach (var c in _clients) c.Writer.TryComplete();
                        _clients.Clear();
                        _idle?.Cancel();
                    }
                }
            }
        }

        private void OnInit(byte[] init)
        {
            lock (this)
            {
                _init = init;
                Broadcast(init);
            }
        }

        private void OnFragment(Fmp4Fragment f)
        {
            lock (this)
            {
                if (f.Keyframe) _gop.Clear();
                _gop.Add(f.Data);
                if (_gop.Count > MaxBacklog) _gop.Clear();
                Broadcast(f.Data);
            }
        }

        // caller holds the lock; never blocks: a full client is dropped
        private void Broadcast(byte[] msg)
        {
            var dropped = false;
            for (var i = _clients.Count - 1; i >= 0; i--)
            {
                var ch = _clients[i];
                if (ch.Writer.TryWrite(msg)) continue;
                _clients.RemoveAt(i);
                while (ch.Reader.TryRead(out _)) { }   // free its backlog; the viewer sees the end at once
                ch.Writer.TryComplete();
                dropped = true;
                owner._log.LogInformation("Relay {Stream} dropped a client {Behind} messages behind", spec.Key, ClientQueue);
            }

            if (dropped) IdleIfEmpty();
        }
    }

    /// <summary>Write-only stream feeding a box reader; a corrupt stream throws InvalidDataException into the source.</summary>
    private sealed class BoxFeedStream(Fmp4BoxReader reader) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => reader.Feed(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) => reader.Feed(buffer);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            reader.Feed(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }
}
