using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using CameraMonitor.Api.Features.Cameras;
using CameraMonitor.Api.Features.Streams;
using CameraMonitor.Api.Infrastructure.Db;
using CameraMonitor.Api.Infrastructure.Xm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CameraMonitor.Api.Features.Events;

/// <summary>
/// Keeps one DVRIP session to an xm camera: on every (re)connect syncs time, enables detection, sets GOP and
/// subscribes to alarms, which become rows in the Events table. Reconnects with backoff on any session loss.
/// </summary>
public sealed class XmCameraAgent(
    CameraConfig cam, string passwordHash, XmAgentStore agents, IServiceScopeFactory scopes, TileService tiles,
    IOptions<ServerOptions> server, TimeProvider clock, ILogger logger)
{
    public static readonly TimeSpan TimeSyncInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan SnapshotCache = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinHealthyRun = TimeSpan.FromSeconds(30);

    private readonly XmAgentState _state = agents.GetOrAdd(cam.Id);
    private readonly ConcurrentDictionary<int, (DateTimeOffset At, byte[] Jpeg)> _snapshots = new();
    private readonly SemaphoreSlim _alarmGate = new(1, 1);   // alarms are handled one at a time, in arrival order
    private TimeZoneInfo? _tz;
    private volatile DvripClient? _client;

    /// <summary>Non-null while connected.</summary>
    public DvripClient? Client => _client;

    public async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var client = new DvripClient(cam.Host, cam.DvripPort, cam.User, passwordHash, clock, logger);
            var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Closed += e => closed.TrySetResult(e);
            DateTimeOffset? connectedAt = null;
            try
            {
                await client.ConnectAsync(ct);
                connectedAt = clock.GetUtcNow();
                _client = client;
                logger.LogInformation("DVRIP {Camera}: connected to {Host}:{Port}", cam.Id, cam.Host, cam.DvripPort);
                await OnConnectedAsync(client, ct);

                var alive = TimeSpan.FromSeconds(Math.Clamp(client.AliveIntervalSeconds, 5, 20));
                var nextSync = clock.GetUtcNow() + TimeSyncInterval;
                while (true)
                {
                    var delay = Task.Delay(alive, clock, ct);
                    if (await Task.WhenAny(delay, closed.Task) == closed.Task)
                    {
                        var error = await closed.Task;
                        throw error ?? new IOException("DVRIP connection closed");
                    }

                    await delay;
                    await client.KeepAliveAsync(ct);
                    if (clock.GetUtcNow() >= nextSync)
                    {
                        await StepAsync("SyncTime", () => SyncTimeAsync(client, ct), ct);
                        nextSync = clock.GetUtcNow() + TimeSyncInterval;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _state.LastError = e.Message;
                logger.LogWarning("DVRIP {Camera}: {Phase} failed: {Error}", cam.Id, connectedAt is null ? "connect" : "session", e.Message);
            }
            finally
            {
                _client = null;
                _state.Connected = false;
                _state.ConnectedSince = null;
                await client.DisposeAsync();
            }

            if (connectedAt is { } since)
            {
                _state.Reconnects++;
                if (clock.GetUtcNow() - since >= MinHealthyRun)
                {
                    attempt = 0;
                }
            }

            try
            {
                await Task.Delay(Backoff.NextDelay(attempt++), clock, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public Task PtzAsync(string command, int dvripChannel, int preset, int step, CancellationToken ct) =>
        (Client ?? throw new InvalidOperationException($"DVRIP {cam.Id} is not connected")).PtzAsync(command, dvripChannel, preset, step, ct);

    public async Task<byte[]?> SnapshotAsync(int dvripChannel, CancellationToken ct)
    {
        if (_snapshots.TryGetValue(dvripChannel, out var cached) && clock.GetUtcNow() - cached.At < SnapshotCache)
        {
            return cached.Jpeg;
        }

        if (Client is not { } client)
        {
            return null;
        }

        try
        {
            var jpeg = await client.SnapshotAsync(dvripChannel, ct);
            _snapshots[dvripChannel] = (clock.GetUtcNow(), jpeg);
            return jpeg;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug("DVRIP {Camera}: snapshot of channel {Channel} failed: {Error}", cam.Id, dvripChannel, e.Message);
            return null;
        }
    }

    public async Task HandleAlarmAsync(AlarmInfo alarm, CancellationToken ct)
    {
        var lens = cam.Lenses.FirstOrDefault(l => l.DvripChannel == alarm.Channel)?.Id ?? $"ch{alarm.Channel}";
        var type = MapEvent(alarm.Event);
        _state.LastAlarmAt = clock.GetUtcNow();
        await _alarmGate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var open = await db.Events.FirstOrDefaultAsync(e => e.CameraId == cam.Id && e.LensId == lens && e.Type == type && e.EndedUtc == null, ct);
            switch (alarm.Status)
            {
                case "Start" when open is null:
                    var jpeg = await SnapshotAsync(alarm.Channel, ct) ?? await tiles.GetAsync(new StreamKey(cam.Id, lens), ct);
                    string? path = null;
                    if (jpeg is not null)
                    {
                        path = $"events/{cam.Id}/{now:yyyyMMdd}/{now:HHmmss}-{lens}-{type}.jpg";
                        try
                        {
                            var file = Path.Combine(server.Value.DataDir, path);
                            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                            await File.WriteAllBytesAsync(file, jpeg, ct);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        {
                            logger.LogWarning("DVRIP {Camera}/{Lens}: event snapshot not saved: {Error}", cam.Id, lens, e.Message);
                            path = null;   // the event itself still counts
                        }
                    }

                    db.Events.Add(new EventRow { CameraId = cam.Id, LensId = lens, Type = type, StartedUtc = now, SnapshotPath = path });
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("DVRIP {Camera}/{Lens}: {Type} started", cam.Id, lens, type);
                    break;
                case "Stop" when open is not null:
                    open.EndedUtc = now;
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("DVRIP {Camera}/{Lens}: {Type} ended", cam.Id, lens, type);
                    break;
                default:
                    logger.LogDebug("DVRIP {Camera}: ignored alarm {Event} {Status} on channel {Channel}", cam.Id, alarm.Event, alarm.Status, alarm.Channel);
                    break;
            }
        }
        finally
        {
            _alarmGate.Release();
        }
    }

    /// <summary>A second, logged-in connection (archive download); the caller disposes it.</summary>
    public async Task<DvripClient> CreateDownloadClientAsync(CancellationToken ct)
    {
        var client = new DvripClient(cam.Host, cam.DvripPort, cam.User, passwordHash, clock, logger);
        await client.ConnectAsync(ct);   // disposes itself on failure
        return client;
    }

    public static JsonNode ApplyDetection(JsonNode detect, IEnumerable<int> channels, out bool changed)
    {
        var result = detect.DeepClone();
        changed = false;
        var list = channels.ToList();
        foreach (var key in new[] { "MotionDetect", "HumanDetection" })
        {
            if (result[key] is not JsonArray arr)
            {
                continue;
            }

            foreach (var ch in list.Where(c => c >= 0 && c < arr.Count))
            {
                if (arr[ch] is not JsonObject o)
                {
                    continue;
                }

                changed |= Set(o, "Enable", true);
                if (o["EventHandler"] is JsonObject handler && handler.ContainsKey("MsgtoNetEnable"))
                {
                    changed |= Set(handler, "MsgtoNetEnable", true);
                }
            }
        }

        return result;
    }

    public static JsonNode ApplyEncode(JsonNode simplifyEncode, IEnumerable<int> channels, out bool changed)
    {
        var result = simplifyEncode.DeepClone();
        changed = false;
        if (result is not JsonArray arr)
        {
            return result;
        }

        foreach (var ch in channels.Where(c => c >= 0 && c < arr.Count))
        {
            if (arr[ch]?["MainFormat"] is not JsonObject main)
            {
                continue;
            }

            changed |= ClearSmart(main);
            if (main["Video"] is not JsonObject video)
            {
                continue;
            }

            changed |= ClearSmart(video);
            if (!(video["GOP"] is JsonValue g && g.TryGetValue<int>(out var gop) && gop == 1))
            {
                video["GOP"] = 1;
                changed = true;
            }

            if (video["Compression"] is JsonValue c && c.TryGetValue<string>(out var compression) && compression.EndsWith('+'))
            {
                video["Compression"] = compression.TrimEnd('+');
                changed = true;
            }
        }

        return result;
    }

    /// <summary>The camera names the event: the result goes into a file name, so only [a-z0-9] survive.</summary>
    public static string MapEvent(string xmEvent)
    {
        if (xmEvent.Contains("Human", StringComparison.Ordinal))
        {
            return "human";
        }

        if (xmEvent.Contains("Motion", StringComparison.Ordinal))
        {
            return "motion";
        }

        var safe = new string([.. xmEvent.ToLowerInvariant().Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))]);
        return safe.Length > 0 ? safe : "event";
    }

    /// <summary>
    /// The camera reconnected, so the previous alarm state is unknown. Holds the alarm gate: a handler of the previous
    /// session still grabbing its snapshot must not insert an open row after this cleanup.
    /// </summary>
    internal async Task CloseOpenEventsAsync(CancellationToken ct)
    {
        await _alarmGate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var n = await db.Events.Where(e => e.CameraId == cam.Id && e.EndedUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.EndedUtc, now), ct);
            logger.LogInformation("DVRIP {Camera}: closed {Count} open events after (re)connect", cam.Id, n);
        }
        finally
        {
            _alarmGate.Release();
        }
    }

    private async Task OnConnectedAsync(DvripClient client, CancellationToken ct)
    {
        var channels = cam.Lenses.Select(l => l.DvripChannel).ToList();
        await StepAsync("SyncTime", () => SyncTimeAsync(client, ct), ct);
        await StepAsync("EnsureDetection", async () =>
        {
            var detect = await client.GetConfigAsync("Detect", ct) ?? throw new DvripException(-1, "Detect config is empty");
            var updated = ApplyDetection(detect, channels, out var changed);
            if (changed)
            {
                await client.SetConfigAsync("Detect", updated, ct);
            }

            logger.LogInformation("DVRIP {Camera}: detection {Result}", cam.Id, changed ? "enabled" : "already enabled");
        }, ct);
        await StepAsync("EnsureEncode", async () =>
        {
            var encode = await client.GetConfigAsync("Simplify.Encode", ct) ?? throw new DvripException(-1, "Simplify.Encode config is empty");
            var updated = ApplyEncode(encode, channels, out var changed);
            if (changed)
            {
                await client.SetConfigAsync("Simplify.Encode", updated, ct);
            }

            var video = updated is JsonArray arr ? string.Join(", ", arr.Select(c => c?["MainFormat"]?["Video"]?.ToJsonString())) : "";
            logger.LogInformation("DVRIP {Camera}: encode {Result}: {Video}", cam.Id, changed ? "updated" : "unchanged", video);
            _state.Gop = 1;
        }, ct);

        // Close stale events before alarms flow (the brief lists this after Guard): an alarm that starts right after
        // Guard must not be closed by this cleanup.
        await StepAsync("CloseOpenEvents", () => CloseOpenEventsAsync(ct), ct);
        await StepAsync("Guard", async () =>
        {
            client.Alarm += alarm => _ = HandleAlarmSafeAsync(alarm, ct);
            await client.GuardAsync(ct);
            logger.LogInformation("DVRIP {Camera}: alarm guard on", cam.Id);
        }, ct);

        _state.Connected = true;
        _state.ConnectedSince = clock.GetUtcNow();
        try
        {
            agents.RaiseConnected(cam.Id);
        }
        catch (Exception e)
        {
            logger.LogError(e, "DVRIP {Camera}: Connected handler failed", cam.Id);
        }
    }

    private async Task SyncTimeAsync(DvripClient client, CancellationToken ct)
    {
        var tz = _tz ??= ResolveTimeZone();
        var camera = await client.GetTimeAsync(ct);
        _state.ClockDriftSeconds = (camera - TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, tz)).TotalSeconds;
        await client.SetTimeAsync(TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, tz), ct);
        logger.LogInformation("DVRIP {Camera}: clock synced, drift was {Drift} s", cam.Id, _state.ClockDriftSeconds);
    }

    private TimeZoneInfo ResolveTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(server.Value.TimeZone);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogError("Unknown Server:TimeZone '{Zone}', camera clocks are set to UTC", server.Value.TimeZone);
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>One OnConnected step: a failure is logged and kept in LastError, and does not block the next step.</summary>
    private async Task StepAsync(string name, Func<Task> step, CancellationToken ct)
    {
        try
        {
            await step();
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _state.LastError = $"{name}: {e.Message}";
            logger.LogWarning("DVRIP {Camera}: {Step} failed: {Error}", cam.Id, name, e.Message);
        }
    }

    private async Task HandleAlarmSafeAsync(AlarmInfo alarm, CancellationToken ct)
    {
        try
        {
            await HandleAlarmAsync(alarm, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }
        catch (Exception e)
        {
            logger.LogError(e, "DVRIP {Camera}: alarm {Event} {Status} failed", cam.Id, alarm.Event, alarm.Status);
        }
    }

    private static bool Set(JsonObject o, string key, bool value)
    {
        if (o[key] is JsonValue v && v.TryGetValue<bool>(out var current) && current == value)
        {
            return false;
        }

        o[key] = value;
        return true;
    }

    private static bool ClearSmart(JsonObject o)
    {
        var changed = false;
        foreach (var key in o.Select(p => p.Key).Where(k => k.StartsWith("Smart", StringComparison.Ordinal)).ToList())
        {
            if (o[key] is not JsonValue v)
            {
                continue;
            }

            switch (v.GetValueKind())
            {
                case JsonValueKind.True:
                    o[key] = false;
                    changed = true;
                    break;
                case JsonValueKind.Number when !(v.TryGetValue<double>(out var d) && d == 0):
                    o[key] = 0;
                    changed = true;
                    break;
            }
        }

        return changed;
    }
}
