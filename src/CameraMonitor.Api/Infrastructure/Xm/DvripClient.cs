using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CameraMonitor.Api.Infrastructure.Xm;

/// <summary>
/// One DVRIP session to a Xiongmai camera (port 34567). Single use: create a new client to reconnect.
/// Requests run one at a time; alarm pushes are routed off the receive loop to <see cref="Alarm"/>.
/// </summary>
public sealed class DvripClient(string host, int port, string user, string passwordHash, TimeProvider clock, ILogger logger) : IAsyncDisposable
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(30);
    public const int PtzStartPreset = 65535;   // python-dvr ptz_step: first message
    public const int PtzStopPreset = -1;       // python-dvr ptz_step: second message  // ponytail: verify on the real camera, swap the two if it moves on release
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(5);
    private const int FilePage = 64, MaxFiles = 512;
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Channel<DvripFrame> _frames = Channel.CreateUnbounded<DvripFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Task _receive = Task.CompletedTask;
    private Task _events = Task.CompletedTask;   // alarm/closed delivery chain, touched only by the receive loop
    private uint _sequence;
    private volatile bool _open, _loggedIn;
    private int _disposed;

    public bool Connected => _open && _loggedIn;
    public uint Session { get; private set; }
    public int AliveIntervalSeconds { get; private set; } = 20;

    /// <summary>Raised in arrival order on a pool thread; handler exceptions are logged.</summary>
    public event Action<AlarmInfo>? Alarm;

    /// <summary>The receive loop of a logged-in session ended; null when DisposeAsync closed it. Raised on a pool thread.</summary>
    public event Action<Exception?>? Closed;

    public async Task ConnectAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_tcp is not null)
        {
            throw new InvalidOperationException("ConnectAsync can be called once; create a new DvripClient to reconnect.");
        }

        _tcp = new TcpClient { NoDelay = true };
        try
        {
            using (var timeout = new CancellationTokenSource(ConnectTimeout, clock))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
            {
                try
                {
                    await _tcp.ConnectAsync(host, port, linked.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"DVRIP connect to {host}:{port} timed out after {ConnectTimeout}.");
                }
            }

            _stream = _tcp.GetStream();
            _open = true;
            _receive = Task.Run(() => ReceiveLoopAsync(_stream));

            var login = new JsonObject
            {
                ["EncryptType"] = "MD5",
                ["LoginType"] = "DVRIP-Web",
                ["PassWord"] = passwordHash,
                ["UserName"] = user,
            };
            var reply = CheckRet(await ExclusiveAsync(RequestTimeout, (_, t) => ExchangeAsync(DvripMessages.Login, login, t), ct, requireLogin: false));
            Session = reply["SessionID"] is JsonValue sid && sid.TryGetValue<string>(out var hex)
                ? Convert.ToUInt32(hex, 16)
                : throw new DvripException(-1, "DVRIP login reply has no SessionID.");
            AliveIntervalSeconds = reply["AliveInterval"] is JsonValue alive && alive.TryGetValue<int>(out var s) && s > 0 ? s : 20;
            _loggedIn = true;
            if (!_open)
            {
                throw new IOException("The camera closed the connection right after login.");
            }
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task<JsonObject> RequestAsync(ushort msgId, JsonObject body, CancellationToken ct) =>
        CheckRet(await RawRequestAsync(msgId, body, ct));

    public Task KeepAliveAsync(CancellationToken ct) =>
        RequestAsync(DvripMessages.KeepAlive, new JsonObject { ["Name"] = "KeepAlive" }, ct);

    public Task GuardAsync(CancellationToken ct) =>
        RequestAsync(DvripMessages.Guard, new JsonObject { ["Name"] = "" }, ct);

    /// <summary>A reply naming another section is a late answer to an earlier, timed-out request: never hand it back as this one.</summary>
    public async Task<JsonNode?> GetConfigAsync(string name, CancellationToken ct)
    {
        var reply = await RequestAsync(DvripMessages.ConfigGet, new JsonObject { ["Name"] = name }, ct);
        return reply["Name"] is JsonValue v && v.TryGetValue<string>(out var got) && got != name
            ? throw new DvripException(-1, $"stale reply: asked for {name}, got {got}")
            : reply[name]?.DeepClone();
    }

    public Task SetConfigAsync(string name, JsonNode value, CancellationToken ct) =>
        RequestAsync(DvripMessages.ConfigSet, new JsonObject { ["Name"] = name, [name] = value.DeepClone() }, ct);

    public async Task<DateTime> GetTimeAsync(CancellationToken ct)
    {
        var reply = await RequestAsync(DvripMessages.TimeQuery, new JsonObject { ["Name"] = "OPTimeQuery" }, ct);
        return ParseTime(reply["OPTimeQuery"]);
    }

    public Task SetTimeAsync(DateTime local, CancellationToken ct) =>
        RequestAsync(DvripMessages.TimeSetting, new JsonObject { ["Name"] = "OPTimeSetting", ["OPTimeSetting"] = FormatTime(local) }, ct);

    public Task PtzAsync(string command, int channel, int preset, int step, CancellationToken ct) =>
        RequestAsync(DvripMessages.PtzControl, new JsonObject
        {
            ["Name"] = "OPPTZControl",
            ["OPPTZControl"] = new JsonObject
            {
                ["Command"] = command,
                ["Parameter"] = new JsonObject
                {
                    ["AUX"] = new JsonObject { ["Number"] = 0, ["Status"] = "On" },
                    ["Channel"] = channel,
                    ["MenuOpts"] = "Enter",
                    ["POINT"] = new JsonObject { ["bottom"] = 0, ["left"] = 0, ["right"] = 0, ["top"] = 0 },
                    ["Pattern"] = "SetBegin",
                    ["Preset"] = preset,
                    ["Step"] = step,
                    ["Tour"] = 0,
                },
            },
        }, ct);

    public Task<byte[]> SnapshotAsync(int channel, CancellationToken ct)
    {
        var body = WithSession(new JsonObject { ["Name"] = "OPSNAP", ["OPSNAP"] = new JsonObject { ["Channel"] = channel } });
        return ExclusiveAsync(SnapshotTimeout, async (_, t) =>
        {
            await SendAsync(DvripMessages.Snapshot, body, t);
            using var data = new MemoryStream();
            while (true)
            {
                var frame = await _frames.Reader.ReadAsync(t);
                if (frame.Payload.IsEmpty)
                {
                    break;
                }

                if (data.Length == 0 && IsJsonAck(frame.Payload.Span))
                {
                    continue;
                }

                data.Write(frame.Payload.Span);
                var buf = data.GetBuffer();
                if (data.Length >= 2 && buf[data.Length - 2] == 0xFF && buf[data.Length - 1] == 0xD9)
                {
                    break;
                }
            }

            var bytes = data.ToArray();
            var jpeg = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0xFE]) ? bytes[16..] : bytes;
            return jpeg.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8])
                ? jpeg
                : throw new DvripException(-1, "not a JPEG");
        }, ct);
    }

    public async Task<IReadOnlyList<XmFileEntry>> QueryFilesAsync(int channel, DateTime beginLocal, DateTime endLocal, CancellationToken ct)
    {
        var result = new List<XmFileEntry>();
        var seen = new HashSet<string>();
        var begin = FormatTime(beginLocal);
        while (true)
        {
            var reply = await RawRequestAsync(DvripMessages.FileQuery, new JsonObject
            {
                ["Name"] = "OPFileQuery",
                ["OPFileQuery"] = new JsonObject
                {
                    ["BeginTime"] = begin,
                    ["Channel"] = channel,
                    ["DriverTypeMask"] = "0x0000FFFF",
                    ["EndTime"] = FormatTime(endLocal),
                    ["Event"] = "*",
                    ["StreamType"] = "0x00000000",
                    ["Type"] = "h264",
                },
            }, ct);
            if (RetOf(reply) is 109 or 119)
            {
                return result;
            }

            var page = CheckRet(reply)["OPFileQuery"] as JsonArray ?? [];
            var added = 0;
            foreach (var item in page)
            {
                if (ParseEntry(item, channel) is { } entry && seen.Add(entry.FileName))
                {
                    result.Add(entry);
                    added++;
                    if (result.Count == MaxFiles)
                    {
                        return result;
                    }
                }
            }

            // ponytail: a page of 64 items that adds nothing new means 64+ files share a BeginTime; stop rather than loop forever
            if (page.Count < FilePage || added == 0 || page[^1]?["BeginTime"] is not JsonValue last || !last.TryGetValue<string>(out var next))
            {
                return result;
            }

            begin = next;
        }
    }

    public async Task DownloadAsync(XmFileEntry file, Stream destination, CancellationToken ct)
    {
        await RequestAsync(DvripMessages.PlayBackClaim, PlayBackBody("Claim", file), ct);
        try
        {
            var start = WithSession(PlayBackBody("DownloadStart", file));
            await ExclusiveAsync(DownloadIdleTimeout, async (idle, t) =>
            {
                await SendAsync(DvripMessages.PlayBack, start, t);
                var first = true;
                while (true)
                {
                    var frame = await _frames.Reader.ReadAsync(t);
                    idle.CancelAfter(DownloadIdleTimeout);
                    if (frame.Payload.IsEmpty || frame.MsgId == DvripMessages.PlayBackEof)
                    {
                        return true;
                    }

                    if (first)
                    {
                        first = false;
                        if (IsJsonAck(frame.Payload.Span))
                        {
                            continue;
                        }
                    }

                    await destination.WriteAsync(frame.Payload, t);
                }
            }, ct);
        }
        finally
        {
            try
            {
                var stop = PlayBackBody("DownloadStop", file);
                stop["OPPlayBack"]!["Parameter"]!["Channel"] = 0;
                await RequestAsync(DvripMessages.PlayBack, stop, CancellationToken.None);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "DVRIP DownloadStop for {File} on {Host} failed", file.FileName, host);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync();
        _tcp?.Dispose();
        await _receive;   // never throws
        _open = false;
        _frames.Writer.TryComplete();
        _stop.Dispose();
        // ponytail: _lock is not disposed, an in-flight request may still Release it
    }

    private Task<JsonObject> RawRequestAsync(ushort msgId, JsonObject body, CancellationToken ct)
    {
        WithSession(body);
        return ExclusiveAsync(RequestTimeout, (_, t) => ExchangeAsync(msgId, body, t), ct);
    }

    /// <summary>
    /// Runs <paramref name="body"/> holding the request lock, after dropping stale frames. The timeout starts now
    /// (synchronously, so a FakeTimeProvider sees it) and covers waiting for the lock; it surfaces as TimeoutException.
    /// </summary>
    private async Task<T> ExclusiveAsync<T>(TimeSpan timeout, Func<CancellationTokenSource, CancellationToken, Task<T>> body, CancellationToken ct, bool requireLogin = true)
    {
        using var timeoutCts = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await _lock.WaitAsync(linked.Token);
            try
            {
                if (!_open || (requireLogin && !_loggedIn))
                {
                    throw new InvalidOperationException($"DVRIP client for {host}:{port} is not connected.");
                }

                while (_frames.Reader.TryRead(out var stale))
                {
                    logger.LogDebug("Dropped stale DVRIP frame {MsgId} from {Host}", stale.MsgId, host);
                }

                return await body(timeoutCts, linked.Token);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"DVRIP request to {host}:{port} timed out after {timeout}.");
        }
    }

    private async Task<JsonObject> ExchangeAsync(ushort msgId, JsonObject body, CancellationToken ct)
    {
        await SendAsync(msgId, body, ct);
        var replyId = (ushort)(msgId + 1);
        while (true)
        {
            var frame = await _frames.Reader.ReadAsync(ct);
            if (frame.MsgId == replyId)
            {
                return DvripCodec.ParseJson(frame.Payload.Span) as JsonObject
                    ?? throw new DvripException(-1, $"Malformed reply to DVRIP message {msgId}.");
            }

            logger.LogDebug("Skipped DVRIP frame {MsgId} from {Host} while waiting for {ReplyId}", frame.MsgId, host, replyId);
        }
    }

    // Called only under _lock.
    private ValueTask SendAsync(ushort msgId, JsonObject body, CancellationToken ct) =>
        _stream!.WriteAsync(DvripCodec.EncodeJson(Session, _sequence++, msgId, body), ct);

    private async Task ReceiveLoopAsync(NetworkStream stream)
    {
        Exception? error;
        try
        {
            var buf = new byte[64 * 1024];
            var len = 0;
            while (true)
            {
                if (len == buf.Length)
                {
                    Array.Resize(ref buf, buf.Length * 2);   // bounded: a frame is at most MaxPayloadBytes + header
                }

                var n = await stream.ReadAsync(buf.AsMemory(len), _stop.Token);
                if (n == 0)
                {
                    throw new IOException("The camera closed the connection.");
                }

                len += n;
                var offset = 0;
                while (true)
                {
                    var rest = buf.AsSpan(offset, len - offset);
                    if (rest.Length >= DvripCodec.HeaderSize && BinaryPrimitives.ReadUInt32LittleEndian(rest[16..]) > MaxPayloadBytes)
                    {
                        throw new InvalidDataException($"DVRIP payload of {BinaryPrimitives.ReadUInt32LittleEndian(rest[16..])} bytes exceeds {MaxPayloadBytes}.");
                    }

                    if (!DvripCodec.TryReadFrame(rest, out var frame, out var consumed))
                    {
                        break;
                    }

                    offset += consumed;
                    Route(frame);
                }

                if (offset > 0)
                {
                    Buffer.BlockCopy(buf, offset, buf, 0, len - offset);
                    len -= offset;
                }
            }
        }
        catch (Exception e)
        {
            error = e;
        }

        _open = false;
        var disposing = Volatile.Read(ref _disposed) != 0;
        if (disposing)
        {
            error = null;
            _frames.Writer.TryComplete();
        }
        else
        {
            _frames.Writer.TryComplete(error);
            logger.LogWarning(error, "DVRIP connection to {Host}:{Port} closed", host, port);
        }

        if (_loggedIn)
        {
            _events = _events.ContinueWith(_ => Raise(Closed, error, nameof(Closed)), TaskScheduler.Default);
        }
    }

    private void Route(DvripFrame frame)
    {
        if (frame.MsgId != DvripMessages.AlarmPush)
        {
            _frames.Writer.TryWrite(frame);
            return;
        }

        if (ParseAlarm(frame.Payload.Span) is { } alarm)
        {
            _events = _events.ContinueWith(_ => Raise(Alarm, alarm, nameof(Alarm)), TaskScheduler.Default);
        }
    }

    private AlarmInfo? ParseAlarm(ReadOnlySpan<byte> payload)
    {
        try
        {
            if (DvripCodec.ParseJson(payload)?["AlarmInfo"] is JsonObject a)
            {
                DateTime? start = a["StartTime"] is JsonValue v && v.TryGetValue<string>(out var s)
                    && DateTime.TryParseExact(s, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
                return new AlarmInfo(a["Channel"]?.GetValue<int>() ?? 0, a["Event"]?.GetValue<string>() ?? "", a["Status"]?.GetValue<string>() ?? "", start);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            logger.LogWarning(e, "Unreadable DVRIP alarm from {Host}", host);
            return null;
        }

        logger.LogWarning("DVRIP alarm from {Host} has no AlarmInfo", host);
        return null;
    }

    private void Raise<T>(Action<T>? handlers, T arg, string name)
    {
        foreach (var handler in handlers?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<T>)handler)(arg);
            }
            catch (Exception e)
            {
                logger.LogError(e, "DVRIP {Event} handler for {Host} failed", name, host);
            }
        }
    }

    private XmFileEntry? ParseEntry(JsonNode? item, int channel)
    {
        try
        {
            var name = item?["FileName"]?.GetValue<string>() ?? throw new FormatException("FileName missing");
            var length = item["FileLength"]?.GetValue<string>() ?? throw new FormatException("FileLength missing");
            return new XmFileEntry(name, ParseTime(item["BeginTime"]), ParseTime(item["EndTime"]), Convert.ToInt64(length, 16), channel);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException or OverflowException)
        {
            logger.LogWarning(e, "Skipping unreadable OPFileQuery item from {Host}", host);
            return null;
        }
    }

    /// <summary>A JSON first frame of a binary stream: OK → true (skip it), not OK → DvripException, not JSON → false.</summary>
    private static bool IsJsonAck(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload[0] != (byte)'{' || DvripCodec.ParseJson(payload) is not JsonObject ack)
        {
            return false;
        }

        CheckRet(ack);
        return true;
    }

    private JsonObject WithSession(JsonObject body)
    {
        body["SessionID"] = $"0x{Session:X8}";
        return body;
    }

    private static JsonObject PlayBackBody(string action, XmFileEntry file) => new()
    {
        ["Name"] = "OPPlayBack",
        ["OPPlayBack"] = new JsonObject
        {
            ["Action"] = action,
            ["Parameter"] = new JsonObject
            {
                ["PlayMode"] = "ByName",
                ["FileName"] = file.FileName,
                ["StreamType"] = 0,
                ["Value"] = 0,
                ["TransMode"] = "TCP",
            },
            ["StartTime"] = FormatTime(file.BeginLocal),
            ["EndTime"] = FormatTime(file.EndLocal),
        },
    };

    private static int RetOf(JsonObject reply) =>
        reply["Ret"] is JsonValue v && v.TryGetValue<int>(out var ret) ? ret : -1;

    private static JsonObject CheckRet(JsonObject reply)
    {
        var ret = RetOf(reply);
        return DvripMessages.OkCodes.Contains(ret) ? reply : throw new DvripException(ret, $"DVRIP error {ret}: {DvripMessages.Describe(ret)}");
    }

    private static string FormatTime(DateTime t) => t.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static DateTime ParseTime(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s)
            ? DateTime.ParseExact(s, TimeFormat, CultureInfo.InvariantCulture)
            : throw new FormatException("DVRIP time missing");
}
