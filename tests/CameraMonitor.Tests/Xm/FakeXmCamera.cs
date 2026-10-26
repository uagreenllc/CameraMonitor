using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using CameraMonitor.Api.Infrastructure.Xm;

namespace CameraMonitor.Tests.Xm;

/// <summary>In-process Xiongmai camera speaking the DVRIP frames DvripClient uses. One task per connection.</summary>
public sealed class FakeXmCamera : IAsyncDisposable
{
    private const uint SessionId = 0x42;
    private const string SessionHex = "0x00000042";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private const ushort PlayBackData = 1426;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Connection> _connections = [];
    private readonly List<Task> _tasks = [];
    private Task _accept = Task.CompletedTask;
    private int _logins, _keepAlives, _fileQueries;
    private volatile bool _guarded, _downloadStopped;

    public int Port { get; private set; }
    public string PasswordHash { get; set; } = DvripCodec.SofiaHash("");
    public int AliveInterval { get; set; } = 21;
    public Dictionary<string, JsonNode> Configs { get; } = [];
    public string CameraTime { get; set; } = "2026-10-26 12:00:00";
    public List<string> SetTimes { get; } = [];
    public List<JsonObject> PtzCommands { get; } = [];
    public List<(XmFileEntry Entry, byte[] Bytes)> Files { get; } = [];
    public byte[] Jpeg { get; set; } = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x02, 0x03, 0xFF, 0xD9];
    public int JpegFrames { get; set; } = 1;
    public int DownloadChunk { get; set; } = 4096;
    public HashSet<ushort> Ignore { get; } = [];

    /// <summary>When set, ConfigGet replies carry this Name (a late reply to an earlier request).</summary>
    public string? ConfigReplyName { get; set; }

    /// <summary>Runs after a request is received and before it is answered (e.g. to interleave an alarm push).</summary>
    public Func<ushort, Task>? BeforeReply { get; set; }

    public int Logins => Volatile.Read(ref _logins);
    public int KeepAlives => Volatile.Read(ref _keepAlives);
    public int FileQueries => Volatile.Read(ref _fileQueries);
    public bool Guarded => _guarded;
    public bool DownloadStopped => _downloadStopped;

    public Task StartAsync()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptLoopAsync();
        return Task.CompletedTask;
    }

    public async Task PushAlarmAsync(int channel, string evt, string status)
    {
        Connection[] targets;
        lock (_connections)
        {
            targets = [.. _connections.Where(c => c.LoggedIn)];
        }

        var body = new JsonObject
        {
            ["AlarmInfo"] = new JsonObject
            {
                ["Channel"] = channel,
                ["Event"] = evt,
                ["StartTime"] = CameraTime,
                ["Status"] = status,
            },
            ["Name"] = "AlarmInfo",
            ["SessionID"] = SessionHex,
        };
        foreach (var c in targets)
        {
            await c.SendAsync(DvripCodec.EncodeJson(SessionId, 0, DvripMessages.AlarmPush, body));
        }
    }

    public void DropConnections()
    {
        lock (_connections)
        {
            foreach (var c in _connections)
            {
                c.Client.Close();
            }

            _connections.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        DropConnections();
        Task[] tasks;
        lock (_tasks)
        {
            tasks = [.. _tasks, _accept];
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // connection tasks end with socket errors once their sockets are closed
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                var conn = new Connection(client);
                lock (_connections)
                {
                    _connections.Add(conn);
                }

                lock (_tasks)
                {
                    _tasks.Add(Task.Run(() => ServeAsync(conn)));
                }
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
        }
    }

    private async Task ServeAsync(Connection conn)
    {
        var stream = conn.Client.GetStream();
        var buf = new byte[64 * 1024];
        var len = 0;
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buf.AsMemory(len), _cts.Token);
                if (n == 0)
                {
                    return;
                }

                len += n;
                int consumed;
                while (DvripCodec.TryReadFrame(buf.AsSpan(0, len), out var frame, out consumed))
                {
                    Buffer.BlockCopy(buf, consumed, buf, 0, len - consumed);
                    len -= consumed;
                    await HandleAsync(conn, frame);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
        }
        finally
        {
            conn.Client.Dispose();
        }
    }

    private async Task HandleAsync(Connection conn, DvripFrame frame)
    {
        var req = DvripCodec.ParseJson(frame.Payload.Span) as JsonObject ?? [];
        var id = frame.MsgId;
        switch (id)
        {
            case DvripMessages.KeepAlive: Interlocked.Increment(ref _keepAlives); break;
            case DvripMessages.FileQuery: Interlocked.Increment(ref _fileQueries); break;
        }

        if (Ignore.Contains(id))
        {
            return;
        }

        if (BeforeReply is { } hook)
        {
            await hook(id);
        }

        var name = req["Name"]?.GetValue<string>() ?? "";
        switch (id)
        {
            case DvripMessages.Login:
                if (req["PassWord"]?.GetValue<string>() != PasswordHash)
                {
                    await ReplyAsync(conn, id, new JsonObject { ["Ret"] = 203 });
                    return;
                }

                Interlocked.Increment(ref _logins);
                conn.LoggedIn = true;
                await ReplyAsync(conn, id, new JsonObject
                {
                    ["AliveInterval"] = AliveInterval,
                    ["ChannelNum"] = 2,
                    ["DeviceType "] = "IPC",
                    ["ExtraChannel"] = 0,
                    ["Ret"] = 100,
                    ["SessionID"] = SessionHex,
                });
                return;
            case DvripMessages.ConfigGet:
                if (!Configs.TryGetValue(name, out var config))
                {
                    await ReplyAsync(conn, id, Ok(name, 607));
                    return;
                }

                await ReplyAsync(conn, id, Ok(ConfigReplyName ?? name, payload: config.DeepClone()));
                return;
            case DvripMessages.ConfigSet:
                Configs[name] = req[name]!.DeepClone();
                break;
            case DvripMessages.TimeQuery:
                await ReplyAsync(conn, id, Ok(name, payload: CameraTime));
                return;
            case DvripMessages.TimeSetting:
                SetTimes.Add(req["OPTimeSetting"]!.GetValue<string>());
                break;
            case DvripMessages.PtzControl:
                PtzCommands.Add((JsonObject)req["OPPTZControl"]!.DeepClone());
                break;
            case DvripMessages.Guard:
                _guarded = true;
                break;
            case DvripMessages.Snapshot:
                await SendSnapshotAsync(conn);
                return;
            case DvripMessages.FileQuery:
                await ReplyAsync(conn, id, QueryFiles(req["OPFileQuery"]!));
                return;
            case DvripMessages.PlayBack:
                var action = req["OPPlayBack"]!["Action"]!.GetValue<string>();
                if (action == "DownloadStart")
                {
                    await SendFileAsync(conn, req["OPPlayBack"]!["Parameter"]!["FileName"]!.GetValue<string>());
                    return;
                }

                if (action == "DownloadStop")
                {
                    _downloadStopped = true;
                }

                break;
        }

        await ReplyAsync(conn, id, Ok(name));
    }

    private JsonObject QueryFiles(JsonNode q)
    {
        var channel = q["Channel"]!.GetValue<int>();
        var begin = ParseTime(q["BeginTime"]!);
        var end = ParseTime(q["EndTime"]!);
        var items = new JsonArray();
        foreach (var (e, _) in Files.Where(f => f.Entry.Channel == channel && f.Entry.BeginLocal < end && f.Entry.EndLocal > begin)
                     .OrderBy(f => f.Entry.BeginLocal).Take(64))
        {
            items.Add(new JsonObject
            {
                ["BeginTime"] = e.BeginLocal.ToString(TimeFormat, CultureInfo.InvariantCulture),
                ["DiskNo"] = 0,
                ["EndTime"] = e.EndLocal.ToString(TimeFormat, CultureInfo.InvariantCulture),
                ["FileLength"] = $"0x{e.LengthKb:X8}",
                ["FileName"] = e.FileName,
                ["SerialNo"] = 0,
            });
        }

        return items.Count == 0 ? Ok("OPFileQuery", 119) : Ok("OPFileQuery", payload: items);
    }

    private async Task SendSnapshotAsync(Connection conn)
    {
        var size = (Jpeg.Length + JpegFrames - 1) / JpegFrames;
        for (var i = 0; i < Jpeg.Length; i += size)
        {
            await conn.SendAsync(Raw(DvripMessages.Snapshot + 1, Jpeg.AsSpan(i, Math.Min(size, Jpeg.Length - i))));
        }
    }

    private async Task SendFileAsync(Connection conn, string fileName)
    {
        var bytes = Files.First(f => f.Entry.FileName == fileName).Bytes;
        for (var i = 0; i < bytes.Length; i += DownloadChunk)
        {
            await conn.SendAsync(Raw(PlayBackData, bytes.AsSpan(i, Math.Min(DownloadChunk, bytes.Length - i))));
        }

        await conn.SendAsync(Raw(PlayBackData, []));
    }

    private static JsonObject Ok(string name, int ret = 100, JsonNode? payload = null)
    {
        var o = new JsonObject { ["Name"] = name, ["Ret"] = ret, ["SessionID"] = SessionHex };
        if (payload is not null)
        {
            o[name] = payload;
        }

        return o;
    }

    private static Task ReplyAsync(Connection conn, ushort requestId, JsonObject body) =>
        conn.SendAsync(DvripCodec.EncodeJson(SessionId, 0, (ushort)(requestId + 1), body));

    private static byte[] Raw(int msgId, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[DvripCodec.HeaderSize + payload.Length];
        frame[0] = 0xFF;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), SessionId);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(14), (ushort)msgId);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(16), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(DvripCodec.HeaderSize));
        return frame;
    }

    private static DateTime ParseTime(JsonNode node) =>
        DateTime.ParseExact(node.GetValue<string>(), TimeFormat, CultureInfo.InvariantCulture);

    private sealed class Connection(TcpClient client)
    {
        private readonly SemaphoreSlim _write = new(1, 1);

        public TcpClient Client { get; } = client;
        public volatile bool LoggedIn;

        public async Task SendAsync(byte[] frame)
        {
            await _write.WaitAsync();
            try
            {
                await Client.GetStream().WriteAsync(frame);
            }
            finally
            {
                _write.Release();
            }
        }
    }
}
