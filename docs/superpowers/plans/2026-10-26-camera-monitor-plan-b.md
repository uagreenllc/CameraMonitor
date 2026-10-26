# CameraMonitor Plan B (M3 DVRIP, M3b microSD backfill, M4 low-latency live) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Talk to the Xiongmai cameras over their native DVRIP protocol (events with snapshots, PTZ, time sync, GOP/detection config), fill archive gaps from each camera's microSD, and show the selected camera with ≤ 2 s glass-to-glass latency through an fMP4-over-WebSocket relay with an MSE player.

**Architecture:** `DvripClient` (a C# port of `OpenIPC/python-dvr`) speaks the 20-byte-header + JSON protocol on port 34567 over one TCP connection per camera, with a single receive loop that routes alarm pushes to an event and everything else to the one in-flight request. `XmCameraAgent` (one per `Vendor: "xm"` camera, run by `XmAgentSupervisor`) logs in, syncs time, enables detection, forces GOP = 1 s and no smart-encode, keeps the session alive and turns `AlarmInfo` pushes into `Events` rows with JPEG snapshots; PTZ and snapshot endpoints call the agent. `SdBackfill` finds gaps in the segment index, asks the camera for its local recordings (`OPFileQuery`), downloads them on a second DVRIP connection, strips the Xiongmai frame headers to Annex-B H.264, remuxes with ffmpeg into 10-s TS segments and inserts rows with the true `StartUtc` and `Source = "sd"` before `DirectoryScanner` can touch them. `LowLatencyRelay` runs one extra ffmpeg per *viewed* lens (RTSP → fragmented MP4 on stdout), `Fmp4BoxReader` splits the stream into an init segment and `moof`+`mdat` fragments, and `/ws/live/{cam}/{lens}` fans them out to browsers, which play them with `MediaSource` in `sequence` mode and fall back to the existing hls.js player.

**Tech Stack:** .NET 9 (ASP.NET Core minimal API + built-in WebSockets, `BackgroundService`, `TimeProvider`, `System.Net.Sockets`, `System.Text.Json.Nodes`), EF Core 9 + SQLite (two new migrations), ffmpeg (external), hls.js 1.7.3 (vendored) + browser MSE, xUnit + FluentAssertions 6.12 + NSubstitute + `FakeTimeProvider`. No new NuGet packages. Reference implementation kept as text in `third_party/python-dvr/` (MIT).

**Spec:** `docs/superpowers/specs/2026-10-25-camera-monitor-design.md` (Ukrainian), sections «Камери: ONVIF і DVRIP», «Живе відео вибраної камери з малою затримкою», goal 6, milestones M3, M3b, M4. Plan A (`docs/superpowers/plans/2026-10-25-camera-monitor-core.md`) defines the types this plan builds on: `StreamKey`, `StreamState`/`StreamStateStore` (`StatusChanged`, `RecDir`, `LiveDir`), `FfmpegArgs.NormalizePath`, `ProcessRunner`, `SegmentRow`/`AppDbContext`, `TileService`, `SafeName`, `Backoff.NextDelay`, `ApiFactory`, `FakeFfmpegProcess`, `FfmpegFactAttribute`. Deferred Plan A items: `docs/superpowers/plans/2026-10-25-camera-monitor-core-deferred.md`.

## Global Constraints

- Everything in Plan A's Global Constraints still holds: `net9.0`, `Nullable`, `ImplicitUsings`, `sealed` + primary constructors, file-scoped namespaces; vertical slices under `Features/`, shared plumbing under `Infrastructure/`; Serilog; options via `AddOptions<T>().BindConfiguration().ValidateOnStart()`; `BackgroundService.ExecuteAsync` never lets an exception escape; Conventional Commits with the trailer `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`; `dotnet format CameraMonitor.sln --verify-no-changes` and `dotnet test` green before every commit.
- Video is never re-encoded: every ffmpeg command in this plan uses `-c:v copy`.
- Each camera stream is pulled by the supervisor exactly once. The only allowed extra RTSP pull is the low-latency relay, at most `Server:MaxRelays` (default `4`) at a time, each stopped 30 s after its last viewer leaves.
- ffmpeg paths: forward slashes, no `:` (`FfmpegArgs.NormalizePath`); on Windows dev `Server:DataDir=./data`. Temp files for the backfill live under `{DataDir}/tmp/sd/`.
- Camera passwords never appear in logs, API responses or test output: the DVRIP layer receives only the `sofia_hash` of the password (`DvripCodec.SofiaHash`), and even that hash is never logged.
- DVRIP port 34567 (`CameraConfig.DvripPort`, default `34567`); DVRIP channels are 0-based: lens channel `N` (RTSP, 1-based) maps to DVRIP channel `N - 1` unless `LensConfig.XmChannel` overrides it.
- Camera clocks hold **local time** in `Server:TimeZone` (`Europe/Kyiv`); every camera-local ↔ UTC conversion goes through `TimeZoneInfo.FindSystemTimeZoneById(server.TimeZone)` (fallback UTC with an error log when the zone is unknown). Camera time strings are `yyyy-MM-dd HH:mm:ss`.
- All user-facing UI strings are Ukrainian; dark theme; pages stay plain HTML + `app.js`, no build step.
- Time comes from the injected `TimeProvider` (`Task.Delay(ts, clock, ct)`, `task.WaitAsync(ts, clock, ct)`); wall-clock `Task.Delay` is not used in services.
- JSON over DVRIP is handled with `System.Text.Json.Nodes` (`JsonObject`/`JsonNode`); DVRIP message bodies are camelCase-agnostic (keys exactly as python-dvr sends them, e.g. `"PassWord"`, `"DeviceType "` with the trailing space in replies).
- Real-camera steps (M3/M4 verification on the camera on the developer's desk) are collected in Task 13; Tasks 1–12 are verified on the mediamtx simulator, `FakeXmCamera` and byte fixtures. The camera's LAN address goes into `deploy/cameras.json` / `appsettings.Development.json` and its password into `deploy/secrets/cam_password` (git-ignored); never into chat, logs or this document.

## Review Focus

1. **A DVRIP frame that is truncated, has a bad magic byte, or a reply that never comes** (camera reboots mid-request) — expected: the request fails with `InvalidDataException`/`TimeoutException` within `DvripClient.RequestTimeout` (10 s), the agent logs and reconnects with `Backoff.NextDelay`, nothing hangs forever. Pinned in Task 2 (`TryReadFrame_rejects_bad_magic`, `TryReadFrame_needs_whole_payload`) and Task 3 (`Request_times_out_when_camera_is_silent`, `Closed_raised_when_camera_drops_connection`).
2. **A camera password with non-ASCII characters** — expected: `sofia_hash` uses the UTF-8 bytes, like python-dvr, so login still succeeds. Pinned in Task 2 (`SofiaHash_matches_python_dvr`, vector `"пароль"`).
3. **A WebSocket viewer that stops reading** (laptop lid closed, mobile tab backgrounded) — expected: the relay drops that client and keeps serving the others; no unbounded memory growth. Pinned in Task 11 (`Slow_client_is_dropped_not_blocking_others`).
4. **A microSD file whose first I-frame carries a garbage timestamp** (camera clock reset to 2000 before our time sync) — expected: the file is skipped with a warning when its start lies more than 1 h outside the gap window, never indexed at the wrong date. Pinned in Task 9 (`File_with_timestamp_outside_gap_is_skipped`).
5. **A backfilled segment that collides with a live segment** (same `HHmmss.ts` name, or overlapping time at the gap edges) — expected: the live segment wins, the sd segment is skipped, no duplicate rows and no overwritten files. Pinned in Task 9 (`Segments_overlapping_live_rows_are_skipped`).

---

### Task 1: hls.js fatal-error recovery in `cam.html` (parked Plan A item)

**Files:**
- Modify: `src/CameraMonitor.Api/wwwroot/cam.html`

**Interfaces:**
- Produces: a `setStatus(text)` helper writing to `<p id="st" class="status">` and a `startHls()` function wrapping the existing hls.js/native player (Task 12 reuses both as the fallback player).

- [ ] **Step 1: Wrap the player and add recovery**

Move the existing hls.js/native-HLS code into `function startHls(note)`; add `<p id="st" class="status"></p>` under the video and `function setStatus(t)`. Inside `startHls`, after `attachMedia`:

```js
hls.on(Hls.Events.ERROR, (_, data) => {
  if (!data.fatal) return;
  setStatus('Перепідключення…');
  if (data.type === Hls.ErrorTypes.MEDIA_ERROR) hls.recoverMediaError();
  else setTimeout(() => hls.startLoad(), 2000);
});
hls.on(Hls.Events.FRAG_BUFFERED, () => setStatus(note || ''));
```

When neither hls.js nor native HLS is available: `setStatus('Браузер не підтримує HLS')`. `app.css`: `.status { color: #aaa; min-height: 1.4em; margin: 4px 0 }`. Call `startHls()` at the bottom (Task 12 replaces this call site).

- [ ] **Step 2: Manual check on the simulator**

Run: `docker compose -f deploy/compose.dev.yml up -d`, `dotnet run --project src/CameraMonitor.Api`, open `http://localhost:8080/cam.html?cam=sim1&lens=main`; `docker compose -f deploy/compose.dev.yml stop cam1`, wait 20 s (status shows «Перепідключення…»), `start cam1` → video resumes within ~15 s without a page reload.

- [ ] **Step 3: Commit**

```bash
git add src/CameraMonitor.Api/wwwroot/cam.html src/CameraMonitor.Api/wwwroot/app.css
git commit -m "fix(web): recover the live HLS player after fatal hls.js errors"
```

---

### Task 2: python-dvr reference copy and `DvripCodec` (framing, `sofia_hash`, JSON payloads)

**Files:**
- Create: `third_party/python-dvr/dvrip.py`, `asyncio_dvrip.py`, `AlarmServer.py`, `README.md`, `LICENSE`, `SOURCE.md`
- Create: `src/CameraMonitor.Api/Infrastructure/Xm/DvripCodec.cs`
- Test: `tests/CameraMonitor.Tests/Xm/DvripCodecTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace CameraMonitor.Api.Infrastructure.Xm;

public readonly record struct DvripFrame(byte Version, uint Session, uint Sequence, byte Total, byte Current, ushort MsgId, ReadOnlyMemory<byte> Payload);

public static class DvripCodec
{
    public const int HeaderSize = 20;
    public static string SofiaHash(string password);                       // MD5(UTF-8) → pairs (b[0]+b[1], b[2]+b[3], …) % 62 → "0-9A-Za-z"; 8 chars
    public static byte[] EncodeJson(uint session, uint sequence, ushort msgId, JsonNode body); // header + UTF-8 JSON + "\n\0"; Length counts the tail
    public static bool TryReadFrame(ReadOnlySpan<byte> buffer, out DvripFrame frame, out int consumed); // false (consumed 0) until header+payload are complete; InvalidDataException when buffer[0] != 0xFF
    public static JsonNode? ParseJson(ReadOnlySpan<byte> payload);         // drops bytes < 0x20 except \t \n \r, trims trailing "\n\0"/whitespace; null (never throws) when not JSON
}
```

Header layout (little-endian, from `struct.pack("BB2xII2xHI", …)` in `dvrip.py`): byte 0 `0xFF`; byte 1 version (`0`); bytes 2–3 reserved; 4–7 session; 8–11 sequence; 12 total fragments; 13 current fragment; 14–15 message id; 16–19 payload length.

- [ ] **Step 1: Copy the reference implementation**

Download from `https://raw.githubusercontent.com/OpenIPC/python-dvr/6ea861a1e99cab5922084a4a0eac6fe2d5b6120d/<file>` for `dvrip.py`, `asyncio_dvrip.py`, `AlarmServer.py`, `README.md`, `LICENSE` into `third_party/python-dvr/`. `SOURCE.md`: repository URL, commit `6ea861a1e99cab5922084a4a0eac6fe2d5b6120d` (2026-09-29), MIT, one line that the Python is kept as the protocol specification for `Infrastructure/Xm` and is never executed by this project.

- [ ] **Step 2: Write the failing tests**

`DvripCodecTests`:
- `SofiaHash_matches_python_dvr` — `[Theory]` `("", "tlJwpbo6")`, `("admin", "6QNMIQGe")`, `("123456", "nTBCS19C")`, `("UaGreen-2026", "yaN2LAiY")`, `("пароль", "iigYAvIp")` (vectors computed with `dvrip.py`'s `sofia_hash`) — Review Focus 2.
- `EncodeJson_login_matches_python_header` — `EncodeJson(0, 0, 1000, {"EncryptType":"MD5","LoginType":"DVRIP-Web","PassWord":"tlJwpbo6","UserName":"admin"})`: bytes 0–13 all `00` except byte 0 `FF`; bytes 14–15 `E8 03`; bytes 16–19 == `payload.Length - 20` (LE); last two bytes `0A 00`; `ParseJson(result[20..])` has `PassWord == "tlJwpbo6"` (python-dvr's exact bytes for this packet are `ff00000000000000000000000000e8035f000000` + JSON with spaces; only the header and the JSON *content* must match, not the whitespace).
- `TryReadFrame_parses_login_reply_fixture` — hex `ff00000042000000000000000000e903800000007b2022416c697665496e74657276616c22203a2032312c20224368616e6e656c4e756d22203a20322c2022446576696365547970652022203a2022495043222c202245787472614368616e6e656c22203a20302c202252657422203a203130302c202253657373696f6e494422203a20223078303030303030343222207d0a00` → `Version 0`, `Session 0x42`, `Sequence 0`, `MsgId 1001`, `Payload.Length 128`, `consumed 148`; `ParseJson` gives `Ret 100`, `SessionID "0x00000042"`, `AliveInterval 21`, and a key `"DeviceType "` (trailing space).
- `TryReadFrame_needs_whole_payload` — the fixture minus its last byte → `false`, `consumed 0`; the fixture twice in one buffer → first frame, `consumed 148`, second parses from `buffer[148..]` — Review Focus 1.
- `TryReadFrame_rejects_bad_magic` — fixture with byte 0 = `0x00` → `InvalidDataException` — Review Focus 1.
- `ParseJson_strips_control_bytes_and_tail` — `{"Ret":100,"Name":"X"}` with a raw `0x01` byte inserted after `100` and the tail `\n\0` → `Ret 100`; bytes `FF D8 FF` → `null`.

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~DvripCodec"`
Expected: FAIL (types missing).

- [ ] **Step 4: Implement `DvripCodec`**

`SofiaHash`: `MD5.HashData(Encoding.UTF8.GetBytes(password))`, then for `i in 0..7`: `chars[(md5[2i] + md5[2i+1]) % 62]`. `EncodeJson` serializes with `JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }` (python sends `ensure_ascii=False`). `TryReadFrame` uses `BinaryPrimitives.ReadUInt32LittleEndian` etc. and copies the payload (`ToArray()`) so the caller's buffer can be reused.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/CameraMonitor.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add third_party src/CameraMonitor.Api/Infrastructure/Xm tests/CameraMonitor.Tests/Xm
git commit -m "feat(xm): DVRIP framing, sofia_hash and payload codec with python-dvr fixtures"
```

---

### Task 3: `DvripClient` and the `FakeXmCamera` test server

**Files:**
- Create: `src/CameraMonitor.Api/Infrastructure/Xm/DvripClient.cs`, `src/CameraMonitor.Api/Infrastructure/Xm/DvripMessages.cs`
- Test: `tests/CameraMonitor.Tests/Xm/FakeXmCamera.cs`, `tests/CameraMonitor.Tests/Xm/DvripClientTests.cs`

**Interfaces:**
- Consumes: `DvripCodec`, `DvripFrame`, `Backoff` is **not** used here (reconnects belong to the agent, Task 4).
- Produces:

```csharp
public static class DvripMessages   // request ids; a reply id is request + 1 (1000 → 1001)
{
    public const ushort Login = 1000, KeepAlive = 1006, ConfigSet = 1040, ConfigGet = 1042, PtzControl = 1400,
        PlayBack = 1420, PlayBackEof = 1423, PlayBackClaim = 1424, FileQuery = 1440, TimeSetting = 1450, TimeQuery = 1452,
        Guard = 1500, AlarmPush = 1504, Snapshot = 1560;
    public static readonly int[] OkCodes = [100, 150, 515];   // python-dvr OK_CODES
    public static string Describe(int ret);                   // python-dvr CODES table text, "Unknown error" when absent
}

public sealed class DvripException(int ret, string message) : Exception(message) { public int Ret { get; } = ret; }
public sealed record AlarmInfo(int Channel, string Event, string Status, DateTime? StartTimeLocal);   // from {"AlarmInfo":{"Channel":0,"Event":"HumanDetect","Status":"Start","StartTime":"2026-10-26 14:03:10"}}
public sealed record XmFileEntry(string FileName, DateTime BeginLocal, DateTime EndLocal, long LengthKb, int Channel);   // FileLength is a hex string in KB

public sealed class DvripClient(string host, int port, string user, string passwordHash, TimeProvider clock, ILogger logger) : IAsyncDisposable
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(30);
    public const int PtzStartPreset = 65535;   // python-dvr ptz_step: first message
    public const int PtzStopPreset = -1;       // python-dvr ptz_step: second message  // ponytail: verify on the real camera, swap the two if it moves on release
    public bool Connected { get; }
    public uint Session { get; }
    public int AliveIntervalSeconds { get; }   // from the login reply ("AliveInterval"), 20 when absent
    public event Action<AlarmInfo>? Alarm;      // raised from the receive loop via Task.Run; handler exceptions are logged
    public event Action<Exception?>? Closed;    // receive loop ended; null when DisposeAsync closed it
    public Task ConnectAsync(CancellationToken ct);                 // TCP connect (ConnectTimeout) + Login; DvripException when Ret ∉ OkCodes
    public Task<JsonObject> RequestAsync(ushort msgId, JsonObject body, CancellationToken ct);   // adds "SessionID":"0x%08X"; one request at a time (SemaphoreSlim); drains stale frames first; RequestTimeout → TimeoutException; Ret ∉ OkCodes → DvripException
    public Task KeepAliveAsync(CancellationToken ct);               // KeepAlive {"Name":"KeepAlive"}
    public Task GuardAsync(CancellationToken ct);                   // Guard {"Name":""}   (python-dvr alarmStart)
    public Task<JsonNode?> GetConfigAsync(string name, CancellationToken ct);          // ConfigGet {"Name":name} → reply[name] (null when absent)
    public Task SetConfigAsync(string name, JsonNode value, CancellationToken ct);     // ConfigSet {"Name":name, name: value}
    public Task<DateTime> GetTimeAsync(CancellationToken ct);                          // TimeQuery {"Name":"OPTimeQuery"} → reply["OPTimeQuery"] "yyyy-MM-dd HH:mm:ss", Kind=Unspecified
    public Task SetTimeAsync(DateTime local, CancellationToken ct);                    // TimeSetting {"Name":"OPTimeSetting","OPTimeSetting":"yyyy-MM-dd HH:mm:ss"}
    public Task PtzAsync(string command, int channel, int preset, int step, CancellationToken ct);
    public Task<byte[]> SnapshotAsync(int channel, CancellationToken ct);
    public Task<IReadOnlyList<XmFileEntry>> QueryFilesAsync(int channel, DateTime beginLocal, DateTime endLocal, CancellationToken ct);
    public Task DownloadAsync(XmFileEntry file, Stream destination, CancellationToken ct);
}
```

Message bodies (exactly as python-dvr builds them; `SessionID` is added by `RequestAsync`):
- Login: `{"EncryptType":"MD5","LoginType":"DVRIP-Web","PassWord":passwordHash,"UserName":user}`; reply `SessionID` is a hex string (`"0x00000042"` → `0x42`) used in the header of every later frame and in every body.
- PTZ: `{"Name":"OPPTZControl","OPPTZControl":{"Command":command,"Parameter":{"AUX":{"Number":0,"Status":"On"},"Channel":channel,"MenuOpts":"Enter","POINT":{"bottom":0,"left":0,"right":0,"top":0},"Pattern":"SetBegin","Preset":preset,"Step":step,"Tour":0}}}`.
- Snapshot: send `{"Name":"OPSNAP","OPSNAP":{"Channel":channel}}` and read raw frames: concatenate payloads until the data ends with `FF D9` or a frame has an empty payload (5 s total, then `TimeoutException`); if the data starts with `00 00 01 FE` skip the 16-byte Xiongmai header; the result must start with `FF D8`, else `DvripException(-1, "not a JPEG")`.
- File query: `{"Name":"OPFileQuery","OPFileQuery":{"BeginTime":b,"Channel":channel,"DriverTypeMask":"0x0000FFFF","EndTime":e,"Event":"*","StreamType":"0x00000000","Type":"h264"}}`; `Ret` 109/119 → empty list (not an error); items in `reply["OPFileQuery"]` (`BeginTime`, `EndTime`, `FileName`, `FileLength` hex KB); while a page has 64 items, repeat with `BeginTime` = last item's `BeginTime`, de-duplicate by `FileName`, stop at 512 items.
- Download: `PlayBackClaim` `{"Name":"OPPlayBack","OPPlayBack":{"Action":"Claim","Parameter":{"PlayMode":"ByName","FileName":f,"StreamType":0,"Value":0,"TransMode":"TCP"},"StartTime":begin,"EndTime":end}}` (Ret checked); then `PlayBack` with `"Action":"DownloadStart"` and the same parameters, after which the camera streams data frames: the first frame's payload may be a JSON ack (`{"Ret":…}`: non-OK → `DvripException`, OK → continue); every other payload is appended to `destination`; stop at an empty payload or `MsgId == PlayBackEof`; `DownloadIdleTimeout` between frames → `TimeoutException`; finally `PlayBack` with `"Action":"DownloadStop"` and `"Channel":0` added to `Parameter` (best effort, errors logged).

Receive loop: one task reads the `NetworkStream` into a growing buffer and calls `TryReadFrame` repeatedly; `MsgId == AlarmPush` → `ParseJson` → `AlarmInfo` from `reply["AlarmInfo"]` → `Alarm` on `Task.Run`; every other frame goes to an unbounded `Channel<DvripFrame>` that the current request reads. EOF or socket error → complete the channel, `Connected = false`, raise `Closed`. Sequence numbers increment per sent frame (python-dvr's counter semantics are irrelevant to the camera).

`FakeXmCamera` (test helper, `TcpListener` on `127.0.0.1:0`, one task per connection, speaks the frames above):

```csharp
public sealed class FakeXmCamera : IAsyncDisposable
{
    public int Port { get; }
    public string PasswordHash { get; set; } = DvripCodec.SofiaHash("");
    public int AliveInterval { get; set; } = 21;
    public Dictionary<string, JsonNode> Configs { get; }            // ConfigGet/ConfigSet by "Name"
    public string CameraTime { get; set; } = "2026-10-26 12:00:00";
    public List<string> SetTimes { get; }                           // OPTimeSetting values received
    public List<JsonObject> PtzCommands { get; }                    // the "OPPTZControl" objects received
    public List<(XmFileEntry Entry, byte[] Bytes)> Files { get; }   // served by OPFileQuery (filtered by Channel and time overlap) and OPPlayBack
    public byte[] Jpeg { get; set; }                                // FF D8 FF E0 … FF D9 (≥ 10 bytes)
    public int JpegFrames { get; set; } = 1;                        // split the JPEG over N frames
    public int DownloadChunk { get; set; } = 4096;
    public HashSet<ushort> Ignore { get; }                          // request ids the fake never answers
    public int Logins { get; } public int KeepAlives { get; } public int FileQueries { get; } public bool Guarded { get; } public bool DownloadStopped { get; }
    public Task StartAsync();
    public Task PushAlarmAsync(int channel, string evt, string status);   // AlarmPush frame to every logged-in session
    public void DropConnections();                                        // closes every socket
}
```

Login reply: `{"AliveInterval":AliveInterval,"ChannelNum":2,"DeviceType ":"IPC","ExtraChannel":0,"Ret":100,"SessionID":"0x00000042"}`; wrong hash → `{"Ret":203}`. Every other JSON reply: `{"Name":…,"Ret":100,"SessionID":"0x00000042"}` plus the named payload. Download: data frames of `DownloadChunk` bytes, then an empty frame.

- [ ] **Step 1: Write the failing tests**

`DvripClientTests` (each test starts a `FakeXmCamera`, `new DvripClient("127.0.0.1", fake.Port, "admin", DvripCodec.SofiaHash(""), clock, NullLogger.Instance)`):
- `Login_sets_session_and_alive_interval` — `Connected true`, `Session 0x42`, `AliveIntervalSeconds 21`, `fake.Logins 1`.
- `Wrong_password_throws_DvripException_203` — `fake.PasswordHash = SofiaHash("other")` → `ConnectAsync` throws `DvripException` with `Ret 203`.
- `Request_times_out_when_camera_is_silent` — `fake.Ignore.Add(DvripMessages.ConfigGet)`, `FakeTimeProvider`; `GetConfigAsync("General")` task not completed after `Advance(9 s)`, throws `TimeoutException` after `Advance(2 s)` — Review Focus 1.
- `Alarm_push_raises_event` — `PushAlarmAsync(1, "HumanDetect", "Start")` → `Alarm` received within 2 s with `Channel 1`, `Event "HumanDetect"`, `Status "Start"`; also while a `GetConfigAsync` is in flight (push before the fake answers) the request still completes and the alarm is raised.
- `GetConfig_returns_named_section` — `Configs["Detect"] = {"MotionDetect":[{"Enable":false}]}` → `GetConfigAsync("Detect")` equals it; `SetConfigAsync("Detect", {"MotionDetect":[{"Enable":true}]})` → `fake.Configs["Detect"]` updated.
- `GetTime_and_SetTime_round_trip` — `GetTimeAsync` == `2026-10-26 12:00:00` (`Kind Unspecified`); `SetTimeAsync(new DateTime(2026,10,26,14,3,10))` → `fake.SetTimes == ["2026-10-26 14:03:10"]`.
- `Snapshot_returns_jpeg_bytes` — `[Theory] JpegFrames = 1` and `3` → bytes equal `fake.Jpeg`.
- `Ptz_sends_python_dvr_payload` — `PtzAsync("DirectionLeft", 1, PtzStartPreset, 5)` → `PtzCommands[0]["Command"] == "DirectionLeft"`, `["Parameter"]["Channel"] == 1`, `["Parameter"]["Preset"] == 65535`, `["Parameter"]["Pattern"] == "SetBegin"`.
- `QueryFiles_pages_by_64` — 70 files on channel 0 inside the range → 70 entries, `fake.FileQueries == 2`; entries outside the range are not returned.
- `Download_reassembles_chunks_and_sends_stop` — a 100 000-byte file, `DownloadChunk 4096` → destination bytes equal, `fake.DownloadStopped true`.
- `Closed_raised_when_camera_drops_connection` — `fake.DropConnections()` → `Closed` raised within 2 s, `Connected false`, a following `KeepAliveAsync` throws `InvalidOperationException` — Review Focus 1.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~DvripClient"`
Expected: FAIL.

- [ ] **Step 3: Implement `DvripMessages`, `DvripClient`, `FakeXmCamera`**

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CameraMonitor.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/CameraMonitor.Api/Infrastructure/Xm tests/CameraMonitor.Tests/Xm
git commit -m "feat(xm): DVRIP client (login, keepalive, alarms, config, time, PTZ, snapshot, file query, download) with a fake camera for tests"
```

---

### Task 4: `Events` table, `XmCameraAgent`, `XmAgentSupervisor`, DVRIP status

**Files:**
- Create: `src/CameraMonitor.Api/Infrastructure/Db/EventRow.cs`, `src/CameraMonitor.Api/Infrastructure/Db/Migrations/<timestamp>_Events.cs` (generated)
- Create: `src/CameraMonitor.Api/Features/Events/XmAgentStore.cs`, `XmCameraAgent.cs`, `XmAgentSupervisor.cs`
- Modify: `Infrastructure/Db/AppDbContext.cs`, `Features/Cameras/CamerasOptions.cs` (`CameraConfig.DvripPort`, `LensConfig.XmChannel`/`DvripChannel`), `Features/Cameras/CamerasEndpoints.cs` (`dvrip` flag), `Features/Status/StatusEndpoints.cs` (`cameras` block), `wwwroot/status.html`, `Program.cs`
- Test: `tests/CameraMonitor.Tests/Events/XmCameraAgentTests.cs`, `tests/CameraMonitor.Tests/Status/StatusEndpointTests.cs`

**Interfaces:**
- Consumes: `DvripClient`, `DvripCodec.SofiaHash`, `CameraPasswords.Resolve`, `TileService.GetAsync` (snapshot fallback), `Backoff.NextDelay`, `AppDbContext`.
- Produces:

```csharp
public sealed class EventRow
{
    public long Id { get; set; }
    public required string CameraId { get; set; }
    public required string LensId { get; set; }
    public required string Type { get; set; }      // "human" | "motion" | other lower-cased camera event names
    public DateTime StartedUtc { get; set; }        // Kind=Utc (same converter as SegmentRow.StartUtc)
    public DateTime? EndedUtc { get; set; }         // null while the alarm is open
    public string? SnapshotPath { get; set; }       // relative to DataDir: events/{cam}/{yyyyMMdd}/{HHmmss}-{lens}-{type}.jpg
}
// AppDbContext: DbSet<EventRow> Events; index (CameraId, StartedUtc)

public sealed class CameraConfig { /* existing */ public int DvripPort { get; set; } = 34567; }
public sealed class LensConfig { /* existing */ public int? XmChannel { get; set; } public int DvripChannel => XmChannel ?? Channel - 1; }   // ponytail: 0-based DVRIP channel = RTSP channel - 1 until the real camera says otherwise

public sealed class XmAgentState
{
    public required string CameraId { get; init; }
    public bool Connected { get; set; }
    public DateTimeOffset? ConnectedSince { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastAlarmAt { get; set; }
    public double? ClockDriftSeconds { get; set; }   // camera time − server time at the last sync
    public int Reconnects { get; set; }
    public int? Gop { get; set; }                     // MainFormat.Video.GOP after EnsureEncode
}
public sealed class XmAgentStore
{
    public event Action<string>? Connected;                       // camera id, raised after OnConnected succeeds (Task 9 subscribes)
    public XmAgentState GetOrAdd(string cameraId); public bool TryGet(string cameraId, out XmAgentState state); public IReadOnlyList<XmAgentState> All(); public bool Remove(string cameraId);
    public void Register(string cameraId, XmCameraAgent agent); public XmCameraAgent? Agent(string cameraId); public void Unregister(string cameraId);
    public void RaiseConnected(string cameraId);
}
public sealed class XmCameraAgent(CameraConfig cam, string passwordHash, XmAgentStore agents, IServiceScopeFactory scopes, TileService tiles, IOptions<ServerOptions> server, TimeProvider clock, ILogger logger)
{
    public static readonly TimeSpan TimeSyncInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan SnapshotCache = TimeSpan.FromSeconds(2);
    public DvripClient? Client { get; }                                   // non-null while connected
    public Task RunAsync(CancellationToken ct);                           // connect → OnConnected → keepalive/time-sync loop until Closed or ct → Backoff.NextDelay(attempt) → repeat; attempt resets after a connection that lived ≥ 30 s
    public Task PtzAsync(string command, int dvripChannel, int preset, int step, CancellationToken ct);   // InvalidOperationException when not connected
    public Task<byte[]?> SnapshotAsync(int dvripChannel, CancellationToken ct);                             // cached SnapshotCache per channel; null when not connected or failed (logged Debug)
    public Task HandleAlarmAsync(AlarmInfo alarm, CancellationToken ct);  // Start → open event (+ snapshot); Stop → close; also used by Task 6
    public Task<DvripClient> CreateDownloadClientAsync(CancellationToken ct);   // a second, logged-in connection (Task 9)
    public static JsonNode ApplyDetection(JsonNode detect, IEnumerable<int> channels, out bool changed);
    public static JsonNode ApplyEncode(JsonNode simplifyEncode, IEnumerable<int> channels, out bool changed);
    public static string MapEvent(string xmEvent);   // contains "Human" → "human"; contains "Motion" → "motion"; else ToLowerInvariant()
}
public sealed class XmAgentSupervisor(IOptionsMonitor<CamerasOptions> cameras, XmAgentStore agents, IServiceScopeFactory scopes, TileService tiles, IOptions<ServerOptions> server, TimeProvider clock, ILoggerFactory loggers) : BackgroundService
// same shape as StreamSupervisor: one agent per Vendor == "xm" camera, keyed by (Id, Host, DvripPort, User, passwordHash, lens channels); 500 ms debounce on config change; stop/start the changed ones
```

`OnConnected` (in order, each step logged, a failure of one step does not block the next; `state.LastError` keeps the last message):
1. `SyncTime`: `camera = GetTimeAsync` → `ClockDriftSeconds = (camera − ConvertTimeFromUtc(now, tz)).TotalSeconds`; `SetTimeAsync(ConvertTimeFromUtc(now, tz))`; repeated every `TimeSyncInterval`.
2. `EnsureDetection`: `GetConfigAsync("Detect")` → `ApplyDetection(detect, lenses.Select(l => l.DvripChannel))` → `SetConfigAsync("Detect", …)` when `changed`. `ApplyDetection` sets, for each existing array among `MotionDetect`, `HumanDetection` and for each channel index that exists in that array: `Enable = true`, and `EventHandler.MsgtoNetEnable = true` when that key exists; keys that are absent are left absent (firmware without `HumanDetection` still gets motion).
3. `EnsureEncode`: `GetConfigAsync("Simplify.Encode")` (an array per channel) → `ApplyEncode` sets `[ch].MainFormat.Video.GOP = 1` (seconds = fps, i.e. one I-frame per second), any key starting with `Smart` directly under `MainFormat` or `MainFormat.Video` to `0`/`false` (by the existing value's kind), and strips a trailing `+` from `MainFormat.Video.Compression` (`"H.264+"` → `"H.264"`); `SetConfigAsync` when `changed`; log the resulting `Video` block once at Information; `state.Gop = 1`.
4. `GuardAsync` → `Client.Alarm += alarm => _ = HandleAlarmAsync(alarm, ct)` (fire-and-forget with try/catch + log).
5. Close every open event of this camera in the DB (`EndedUtc = now`) — the camera reconnected, so the previous alarm state is unknown.
6. `state.Connected = true`, `ConnectedSince = now`, `agents.RaiseConnected(cam.Id)`.
Then loop: `Task.Delay(Math.Clamp(Client.AliveIntervalSeconds, 5, 20) s, clock, ct)` → `KeepAliveAsync` (a failure ends the generation); every `TimeSyncInterval` → `SyncTime`.

`HandleAlarmAsync`: `lens = cam.Lenses.FirstOrDefault(l => l.DvripChannel == alarm.Channel)?.Id ?? $"ch{alarm.Channel}"`, `type = MapEvent(alarm.Event)`, `state.LastAlarmAt = now`. `Status == "Start"`: if an open event (`EndedUtc == null`) for `(cam.Id, lens, type)` exists → ignore; else snapshot = `SnapshotAsync(alarm.Channel)` ?? `tiles.GetAsync(new StreamKey(cam.Id, lens))`; write it to `{DataDir}/events/{cam}/{yyyyMMdd}/{HHmmss}-{lens}-{type}.jpg` (UTC names), insert `EventRow { StartedUtc = now }`. `Status == "Stop"`: set `EndedUtc = now` on the open event (ignore when none). Everything else logged at Debug.

`/api/cameras` adds per camera `"dvrip": true|false` (`XmAgentStore.TryGet(...).Connected`, `false` for non-xm). `/api/status` adds `"cameras": [{ "camera", "connected", "connectedSince", "lastError", "lastAlarmAt", "clockDriftSeconds", "reconnects", "gop" }]`; `status.html` gets a panel «Камери (DVRIP)» with one line per camera: «підключено з …» / «не підключено: {lastError}», «зсув годинника: N с», «GOP: 1 с».

`ApiFactory`: nothing new; tests that need an xm camera pass `Host = "127.0.0.1"`, `DvripPort = fake.Port` through `WithCameras`.

- [ ] **Step 1: Write the failing tests**

`XmCameraAgentTests` (a `FakeXmCamera` per test; `ApiFactory().WithoutHostedServices()` for the DB; the agent is constructed directly with `cam = { Id "c1", Host "127.0.0.1", DvripPort fake.Port, Lenses [fixed ch 1, ptz ch 2 Ptz] }`, `FakeTimeProvider` at `2026-10-26T10:00:00Z`, `TimeZone "Europe/Kyiv"`; run with `Task.Run(() => agent.RunAsync(cts.Token))`; `Until(...)` helper as in `StreamWorkerTests`):
- `Connect_syncs_time_enables_detection_sets_gop_and_guards` — fake `Configs["Detect"] = {"MotionDetect":[{"Enable":false,"EventHandler":{"MsgtoNetEnable":false}},{"Enable":false}],"HumanDetection":[{"Enable":false},{"Enable":false}]}`, `Configs["Simplify.Encode"] = [{"MainFormat":{"Video":{"FPS":15,"GOP":4,"Compression":"H.264+","SmartH264":1}}},{"MainFormat":{"Video":{"FPS":15,"GOP":2,"Compression":"H.264"}}}]`, `CameraTime "2026-10-26 12:00:05"` → `fake.SetTimes == ["2026-10-26 12:00:00"]` (10:00 UTC is 12:00 Kyiv on 2026-10-26), `state.ClockDriftSeconds == 5`, every `Enable` and `MsgtoNetEnable` in `Configs["Detect"]` is `true`, both `GOP == 1`, `Compression == "H.264"`, `SmartH264 == 0`, `fake.Guarded`, `state.Connected`, `state.Gop == 1`.
- `ApplyEncode_reports_no_change_when_already_set` — GOP 1, no Smart keys → `changed false`, node unchanged.
- `ApplyDetection_skips_missing_arrays` — `{"MotionDetect":[{"Enable":false}]}` with channels `[0, 1]` → only `[0].Enable` set, no exception, `changed true`.
- `MapEvent_maps_known_names` — `[Theory]` `("HumanDetect","human")`, `("VideoMotion","motion")`, `("AppEventHumanDetectAlarm","human")`, `("VideoLoss","videoloss")`.
- `Alarm_start_creates_event_with_snapshot_and_stop_closes_it` — `PushAlarmAsync(1, "HumanDetect", "Start")` → one `EventRow` with `LensId "ptz"`, `Type "human"`, `EndedUtc null`, `SnapshotPath` = `events/c1/20261026/100000-ptz-human.jpg`, the file exists under `DataDir` and starts with `FF D8`; `PushAlarmAsync(1, "HumanDetect", "Stop")` → `EndedUtc == now`.
- `Duplicate_start_does_not_create_second_open_event` — two `Start` pushes → one row.
- `Reconnect_closes_open_events_and_increments_reconnects` — open event seeded; `fake.DropConnections()`; advance 2 s → `fake.Logins == 2`, the event has `EndedUtc`, `state.Reconnects == 1`.
- `Ptz_throws_when_disconnected` — `fake.Ignore.Add(DvripMessages.Login)`… simpler: agent constructed, not run → `PtzAsync` throws `InvalidOperationException`.
`StatusEndpointTests.Status_has_cameras_block` — `/api/status` → `cameras` array present (empty with no xm cameras); `CamerasEndpointTests.Cameras_report_dvrip_flag` — each camera has `dvrip` (false for the simulator cameras).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~Events|FullyQualifiedName~Status|FullyQualifiedName~CamerasEndpoint"`
Expected: FAIL.

- [ ] **Step 3: Implement, generate the migration**

```bash
dotnet ef migrations add Events --project src/CameraMonitor.Api --output-dir Infrastructure/Db/Migrations
```

Register in `Program.cs`: `AddSingleton<XmAgentStore>()`, `AddHostedService<XmAgentSupervisor>()`. Request logging: `/events` and `/api/snapshot` join the Debug list.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CameraMonitor.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(events): DVRIP camera agent with time sync, detection and GOP config, alarms to an events table, status"
```

---

### Task 5: PTZ, snapshot and events endpoints; `events.html`; PTZ pad and presets on `cam.html`; timeline events; event retention

**Files:**
- Create: `src/CameraMonitor.Api/Features/Events/PtzEndpoints.cs`, `src/CameraMonitor.Api/Features/Events/EventsEndpoints.cs`, `src/CameraMonitor.Api/wwwroot/events.html`
- Modify: `Features/Archive/ArchiveEndpoints.cs` (`/api/timeline` events), `Features/Archive/RetentionService.cs` (events), `Features/Streams/LiveEndpoints.cs` (`/api/tile` source switch), `Features/Cameras/ServerOptions.cs` (`TileSource`), `wwwroot/cam.html`, `wwwroot/archive.html` (`?t=` + event markers), `wwwroot/app.js` (nav «Події»), `wwwroot/app.css`, `Program.cs`
- Test: `tests/CameraMonitor.Tests/Events/PtzEndpointTests.cs`, `EventsEndpointTests.cs`, `tests/CameraMonitor.Tests/Archive/RetentionServiceTests.cs` (one new test)

**Interfaces:**
- Consumes: `XmAgentStore.Agent(camId)`, `XmCameraAgent.PtzAsync/SnapshotAsync`, `DvripClient.PtzStartPreset/PtzStopPreset`, `LensConfig.DvripChannel`, `EventRow`, `SafeName`.
- Produces:

```csharp
public sealed record PtzRequest(string Command, string Action);     // Command ∈ DirectionUp|DirectionDown|DirectionLeft|DirectionRight|DirectionLeftUp|DirectionLeftDown|DirectionRightUp|DirectionRightDown|ZoomTile|ZoomWide ; Action ∈ start|stop
public sealed record PresetRequest(string Action, int Preset);      // Action ∈ set|goto|clear → SetPreset|GotoPreset|ClearPreset ; Preset 1..255
public sealed class ServerOptions { /* existing */ public string TileSource { get; set; } = "ffmpeg"; }   // "ffmpeg" | "dvrip"
```

Endpoints (all ids through `SafeName.IsSafe` → 400; Ukrainian `ProblemDetails` like `ArchiveEndpoints.Bad`):
- `POST /api/ptz/{cam}/{lens}` body `PtzRequest` → 400 unknown command/action; 404 unknown camera/lens or `!lens.Ptz`; 503 «Камера не підключена по DVRIP» when `agents.Agent(cam)?.Client is null`; else `agent.PtzAsync(command, lens.DvripChannel, action == "start" ? PtzStartPreset : PtzStopPreset, 5)` → 204.
- `POST /api/ptz/{cam}/{lens}/preset` body `PresetRequest` → same checks; `PtzAsync("SetPreset"|"GotoPreset"|"ClearPreset", channel, preset, 5)` → 204.
- `GET /api/snapshot/{cam}/{lens}.jpg` → 503/404 as above; `image/jpeg`, `Cache-Control: no-store`.
- `GET /api/tile/{cam}/{lens}.jpg` (existing): when `server.TileSource == "dvrip"` and the camera's agent is connected → `agent.SnapshotAsync(lens.DvripChannel)` (full-size JPEG); else the existing `TileService.GetAsync`. `// ponytail: measured on the real camera in Task 13; the default stays ffmpeg until then`.
- `GET /api/events?cam=&day=yyyy-MM-dd&limit=200` (`cam` optional, `day` defaults to today UTC) → `[{ "id", "camera", "lens", "type", "startedUtc", "endedUtc", "snapshot": "/events/{id}.jpg" | null }]` newest first.
- `GET /events/{id}.jpg` → the row's `SnapshotPath` resolved under `{DataDir}/events` with the same root check as `/seg`; 404 when the row or file is missing; `Cache-Control: private, max-age=86400`.
- `GET /api/timeline` (existing) → `"events": [{ "type", "lens", "from", "to" }]` for rows of that camera **and lens** with `StartedUtc` in the UTC day (`to` null while open).
- `RetentionService.RunOnceAsync`: also delete events with `StartedUtc < now − RetentionDays` (files first, ignore missing; rows after); log the count.

Pages:
- `events.html` («Події»): camera `<select>` («усі» + each), `<input type="date">` (today), list of cards: thumbnail (`/events/{id}.jpg`, placeholder «без знімка»), type label («людина» / «рух» / raw type), «{camera name} · {lens}», «{started} – {ended | триває}»; click → `archive.html?cam=&lens=&t={startedUtc}`; refresh every 10 s; nav gets «Події» between «Архів» and «Статус».
- `archive.html`: accept `?t=ISO` → set the date to that local day, load the timeline, then `play(new Date(t))`; draw event markers from `timeline.events` as 2-px `#f85149` ticks (`title` = type + time).
- `cam.html`: when the lens has `ptz` (from `/api/cameras`), show a pad: 3×3 buttons (↖ ↑ ↗ / ◀ · ▶ / ↙ ↓ ↘ → the eight `Direction*` commands) and «Zoom +» (`ZoomTile`) / «Zoom −» (`ZoomWide`); `pointerdown` → `POST start`, `pointerup`/`pointercancel`/`pointerleave` → `POST stop` (one stop per press, also on `window` `blur`); presets: `<input type="number" min="1" max="255" value="1">` + «Перейти» / «Зберегти» / «Видалити»; HTTP errors (`detail`) shown through `setStatus`. `app.css`: `.pad { display: grid; grid-template-columns: repeat(3, 48px); gap: 4px }`, `.pad button, .presets button { font-size: 1.1rem; padding: 8px }`.

- [ ] **Step 1: Write the failing tests**

`PtzEndpointTests` (`ApiFactory().WithCameras(xm camera "c1" at 127.0.0.1:fake.Port with lenses fixed ch 1 / ptz ch 2 Ptz)`; the hosted `XmAgentSupervisor` connects; wait `Until(fake.Logins == 1)`):
- `Ptz_start_and_stop_reach_camera` — `POST {Command:"DirectionLeft",Action:"start"}` then `stop` → 204 twice; `fake.PtzCommands` Presets `[65535, -1]`, both `Parameter.Channel == 1`.
- `Preset_goto_sends_GotoPreset` — `{Action:"goto",Preset:3}` → `Command "GotoPreset"`, `Preset 3`.
- `Ptz_on_fixed_lens_is_404`, `Ptz_unknown_command_is_400`, `Ptz_when_agent_disconnected_is_503` (camera config with `DvripPort` of a closed port).
- `Snapshot_endpoint_returns_jpeg` — 200, `image/jpeg`, body == `fake.Jpeg`.
- `Tile_uses_dvrip_when_configured` — `WithSetting("Server:TileSource", "dvrip")` → `/api/tile/c1/fixed.jpg` body == `fake.Jpeg`.
`EventsEndpointTests` (seed `EventRow`s through `AppDbContext`): `Events_list_newest_first_with_snapshot_url`; `Events_jpg_serves_file_and_404s_when_missing`; `Timeline_includes_events_of_the_lens_only`.
`RetentionServiceTests.Old_events_and_snapshots_are_deleted` — row 20 d old with a snapshot file, row 1 d old → after `RunOnceAsync` only the young one remains and the old file is gone.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~Ptz|FullyQualifiedName~EventsEndpoint|FullyQualifiedName~Retention"`
Expected: FAIL.

- [ ] **Step 3: Implement endpoints, retention, pages**

- [ ] **Step 4: Run tests; manual check**

Run: `dotnet test tests/CameraMonitor.Tests` → PASS. With the simulator: `/events.html` renders (empty); `/cam.html?cam=sim1&lens=main` shows no pad (not PTZ); a temporary xm camera entry pointing at a closed port shows the pad and «Камера не підключена по DVRIP» on press.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(events): PTZ pad and presets, DVRIP snapshots, events page and timeline markers, event retention"
```

---

### Task 6: Alarm-server fallback (`XmAlarmServer`) for firmware that does not push alarms on the control session

**Files:**
- Create: `src/CameraMonitor.Api/Features/Events/XmAlarmServer.cs`
- Modify: `Features/Cameras/ServerOptions.cs` (`XmAlarmServerPort`, `XmAlarmServerHost`), `Features/Events/XmCameraAgent.cs` (`EnsureAlarmServer` step), `Program.cs`, `docs/SETUP.md` is updated in Task 13
- Test: `tests/CameraMonitor.Tests/Events/XmAlarmServerTests.cs`

**Interfaces:**
- Consumes: `DvripCodec.TryReadFrame/ParseJson`, `XmAgentStore.Agent`, `XmCameraAgent.HandleAlarmAsync`, `IOptionsMonitor<CamerasOptions>` (camera by `Host`).
- Produces:

```csharp
public sealed class ServerOptions { /* existing */ public int XmAlarmServerPort { get; set; } = 0; /* 0 = disabled */ public string? XmAlarmServerHost { get; set; } /* this server's LAN IP as the cameras must reach it */ }
public sealed class XmAlarmServer(IOptionsMonitor<CamerasOptions> cameras, XmAgentStore agents, IOptions<ServerOptions> server, ILogger<XmAlarmServer> logger) : BackgroundService
// TcpListener on 0.0.0.0:XmAlarmServerPort (only when > 0); per connection: read frames for up to 5 s; each frame with a JSON body containing "AlarmInfo" → AlarmInfo → the agent whose CameraConfig.Host equals the remote IP → HandleAlarmAsync; unknown IP → Warning; then close. Listener errors never stop the host (logged, retried after 10 s).
```

`XmCameraAgent.OnConnected` gains step 3b `EnsureAlarmServer` (only when `XmAlarmServerHost` is set and `XmAlarmServerPort > 0`): `GetConfigAsync("NetWork.AlarmServer")` → set `Enable = true`, `Address = XmAlarmServerHost`, `Port = XmAlarmServerPort`, `Protocol = "TCP"` (only keys that exist, plus `Enable/Address/Port` which are added when missing) → `SetConfigAsync` when changed.

- [ ] **Step 1: Write the failing tests**

`XmAlarmServerTests` (`ApiFactory().WithSetting("Server:XmAlarmServerPort", "<free port>").WithCameras(xm camera "c1" Host "127.0.0.1" DvripPort <closed port>)`; the agent is registered but disconnected):
- `Alarm_frame_from_known_host_creates_event` — connect with `TcpClient` to the port, send `EncodeJson(0, 0, 1504, {"Name":"AlarmInfo","AlarmInfo":{"Channel":0,"Event":"VideoMotion","Status":"Start","StartTime":"2026-10-26 12:00:00"}})` → within 2 s one `EventRow` (`LensId "fixed"`, `Type "motion"`, `SnapshotPath null`).
- `Alarm_frame_from_unknown_host_is_ignored` — camera Host `10.0.0.9` → no row.
- `Agent_configures_alarm_server_on_connect` (`FakeXmCamera` with `Configs["NetWork.AlarmServer"] = {"Address":"0.0.0.0","Enable":false,"Port":15002,"Protocol":"TCP"}`, `XmAlarmServerHost "192.168.50.2"`, port 15002) → after connect the config has `Enable true`, `Address "192.168.50.2"`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~XmAlarmServer"`
Expected: FAIL.

- [ ] **Step 3: Implement**

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CameraMonitor.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(events): alarm-server fallback for cameras that only push alarms to a configured receiver"
```

---

### Task 7: `Source` column on `Segments`, `StartUtc` index, timeline by source

**Files:**
- Create: `src/CameraMonitor.Api/Infrastructure/Db/Migrations/<timestamp>_SegmentSource.cs` (generated)
- Modify: `Infrastructure/Db/SegmentRow.cs`, `Infrastructure/Db/AppDbContext.cs`, `Features/Archive/ArchiveEndpoints.cs` (`/api/timeline`), `wwwroot/archive.html`, `wwwroot/app.css`
- Test: `tests/CameraMonitor.Tests/Db/SegmentsDbTests.cs`, `tests/CameraMonitor.Tests/Archive/ArchiveEndpointTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed class SegmentRow { /* existing */ public string Source { get; set; } = "live"; }   // "live" | "sd"; column default 'live' so existing rows stay live
// AppDbContext: e.Property(s => s.Source).HasDefaultValue("live"); e.HasIndex(s => s.StartUtc);
```

`/api/timeline` → `"ranges": [{ "from", "to", "source": "live"|"sd" }]`: rows grouped by `Source`, `TimelineBuilder.Merge` per group, concatenated and ordered by `from`. `archive.html` draws `.range.sd` in `#8a6d1f` (amber) with `title` suffix «(з картки)». Legend line under the timeline: «■ запис сервера ■ з microSD камери».

- [ ] **Step 1: Write the failing tests**

`SegmentsDbTests.Source_defaults_to_live_and_round_trips` — insert without `Source` → reads back `"live"`; insert `Source = "sd"` → `"sd"`.
`ArchiveEndpointTests.Timeline_reports_sd_ranges_separately` — seed two contiguous live rows then one contiguous `sd` row → two ranges, `source` `live` then `sd`, `sd.from == 14:00:20Z`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~SegmentsDb|FullyQualifiedName~Timeline"`
Expected: FAIL.

- [ ] **Step 3: Implement, generate the migration**

```bash
dotnet ef migrations add SegmentSource --project src/CameraMonitor.Api --output-dir Infrastructure/Db/Migrations
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CameraMonitor.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(archive): segment Source column (live|sd), StartUtc index, timeline ranges by source"
```

---

### Task 8: `XmFrameParser` (Xiongmai frame stream → Annex-B) and `SdRemuxer` (Annex-B → 10-s TS segments)

**Files:**
- Create: `src/CameraMonitor.Api/Infrastructure/Xm/XmFrameParser.cs`, `src/CameraMonitor.Api/Features/Backfill/SdRemuxer.cs`
- Test: `tests/CameraMonitor.Tests/Xm/XmFrameParserTests.cs`, `tests/CameraMonitor.Tests/Xm/XmTestStream.cs` (helper), `tests/CameraMonitor.Tests/Backfill/SdRemuxerTests.cs`

**Interfaces:**
- Consumes: `ProcessRunner.RunAsync`, `ServerOptions.FfmpegPath/SegmentSeconds`.
- Produces:

```csharp
public enum XmFrameType : byte { IFrame = 0xFC, PFrame = 0xFD, Audio = 0xFA, Info = 0xF9, Jpeg = 0xFE }
public sealed record XmFrameInfo(XmFrameType Type, DateTime? CameraTime, int Fps, int Width, int Height);
public sealed class XmFrameParser
{
    public static DateTime UnpackTime(uint packed);   // second = v & 0x3F; minute = (v >> 6) & 0x3F; hour = (v >> 12) & 0x1F; day = (v >> 17) & 0x1F; month = (v >> 22) & 0xF; year = (v >> 26) + 2000; Kind=Unspecified; invalid fields → ArgumentOutOfRangeException
    public XmFrameInfo? FirstIFrame { get; }
    public int VideoFrames { get; }
    public void Feed(ReadOnlySpan<byte> data, Stream annexB);   // I/P payloads → annexB; audio/info/jpeg payloads dropped; partial frames buffered between calls; unknown 4-byte magic → InvalidDataException
}
public sealed class XmStripStream(XmFrameParser parser, Stream annexB) : Stream   // write-only: Write → parser.Feed(data, annexB); used as DvripClient.DownloadAsync's destination
public sealed class SdRemuxer(IOptions<ServerOptions> server, ILogger<SdRemuxer> logger)
{
    public sealed record RemuxedSegment(string Path, TimeSpan Start, TimeSpan End);
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);
    public Task<IReadOnlyList<RemuxedSegment>> RemuxAsync(string annexBPath, int fps, string outDir, CancellationToken ct);   // non-zero exit → InvalidOperationException with the exit code
    public static IReadOnlyList<RemuxedSegment> ParseSegmentList(string csv, string outDir);   // "name,start,end" lines
}
```

Frame headers (big-endian magic `00 00 01 FC` etc., then little-endian fields, from `reassemble_bin_payload` in `dvrip.py`): I-frame and JPEG: 16 bytes — magic(4) media(1) fps(1) width/8(1) height/8(1) packedTime(4) length(4); P-frame: 8 bytes — magic(4) length(4); audio: 8 bytes — magic(4) media(1) sampleRate(1) length(2); info: 8 bytes — magic(4) media(1) n(1) length(2). `length` bytes of payload follow each header; video payloads are Annex-B NAL units.

ffmpeg command (verified on ffmpeg 9.0.1 and 5.1 syntax): `-nostdin -hide_banner -loglevel error -fflags +genpts -r {fps} -f h264 -i {annexBPath} -c:v copy -an -f segment -segment_time {SegmentSeconds} -segment_format mpegts -segment_list {outDir}/segments.csv -segment_list_type csv {outDir}/%05d.ts` (no `-reset_timestamps`: PTS stay continuous across a file's segments, so only the first segment of a file is a discontinuity). All paths through `NormalizePath`.

- [ ] **Step 1: Write the failing tests**

`XmTestStream` helper: `IFrame(DateTime t, int fps, int w, int h, byte[] payload)`, `PFrame(byte[] payload)`, `Audio(byte[] payload)` return the framed bytes.
`XmFrameParserTests`:
- `UnpackTime_decodes_python_dvr_packing` — `0x6AB4E0CA` → `2026-10-26 14:03:10`.
- `Feed_strips_headers_in_any_chunking` — stream = I(2026-10-26 14:03:10, 15, 1280, 720, `00 00 00 01 67 AA`) + P(`00 00 00 01 41 BB`) + Audio(`01 02`) + P(`00 00 00 01 41 CC`), fed in 1-, 7- and whole-buffer chunks → annexB == the three video payloads concatenated, `FirstIFrame == (IFrame, 14:03:10, 15, 1280, 720)`, `VideoFrames == 3`.
- `Unknown_magic_throws` — `00 00 01 00 …` → `InvalidDataException`.
- `XmStripStream_feeds_parser` — writing the stream through `XmStripStream` yields the same annexB.
`SdRemuxerTests`:
- `ParseSegmentList_reads_csv` — `"00000.ts,0.000000,10.000000\n00001.ts,10.000000,20.000000\n00002.ts,20.000000,25.000000\n"` → 3 entries with paths under `outDir`.
- `[FfmpegFact] Remux_cuts_annexb_into_10s_segments` — generate `raw.264` with `ffmpeg -f lavfi -i testsrc2=size=320x240:rate=15 -t 25 -c:v libx264 -preset veryfast -g 15 -bf 0 -pix_fmt yuv420p -f h264`, wrap it as one `IFrame` through `XmFrameParser` into `in.264` (round trip), `RemuxAsync(in.264, 15, outDir)` → 3 segments `(0,10)`, `(10,20)`, `(20,25)` (±0.2 s), each file > 0 bytes, `ffprobe` duration of the first ≈ 10 s.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~XmFrame|FullyQualifiedName~SdRemuxer"`
Expected: FAIL.

- [ ] **Step 3: Implement**

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CameraMonitor.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(backfill): Xiongmai frame-stream parser and ffmpeg remux of raw H.264 into 10-s TS segments"
```

---

### Task 9: `GapFinder`, `SdBackfill` service, backfill status

**Files:**
- Create: `src/CameraMonitor.Api/Features/Backfill/GapFinder.cs`, `SdBackfill.cs`, `BackfillStatus.cs`
- Modify: `Features/Cameras/ServerOptions.cs` (`SdLoopDays`, `SdMinGapSeconds`), `Features/Status/StatusEndpoints.cs` (`backfill` block), `wwwroot/status.html`, `Program.cs`
- Test: `tests/CameraMonitor.Tests/Backfill/GapFinderTests.cs`, `SdBackfillTests.cs`

**Interfaces:**
- Consumes: `XmAgentStore` (`Connected` event, `Agent(camId).CreateDownloadClientAsync`), `DvripClient.QueryFilesAsync/DownloadAsync`, `XmFrameParser`/`XmStripStream`, `SdRemuxer`, `StreamStateStore` (`StatusChanged`, `RecDir`), `SegmentRow.Source`, `LensConfig.DvripChannel`.
- Produces:

```csharp
public sealed class ServerOptions { /* existing */ public int SdLoopDays { get; set; } = 3; public int SdMinGapSeconds { get; set; } = 120; }
public sealed record Gap(DateTime FromUtc, DateTime ToUtc);
public static class GapFinder
{
    public static IReadOnlyList<Gap> Find(IEnumerable<(DateTime StartUtc, int DurationMs)> orderedByStart, DateTime windowFromUtc, DateTime windowToUtc, TimeSpan minGap);
    // holes > minGap between consecutive segments clipped to the window, plus windowFrom→first start and last end→windowTo when > minGap; newest first (the card overwrites the oldest recordings first)
}
public sealed class CameraBackfill { public required string CameraId { get; init; } public int Queued { get; set; } public string? Current { get; set; } /* "ptz 14:03–14:20" */ public int FilesDone { get; set; } public int SegmentsAdded { get; set; } public int GapsNoFiles { get; set; } public string? LastError { get; set; } public DateTimeOffset? LastRunUtc { get; set; } }
public sealed class BackfillStatus { public CameraBackfill GetOrAdd(string cameraId); public IReadOnlyList<CameraBackfill> All(); }
public sealed class SdBackfill(IServiceScopeFactory scopes, XmAgentStore agents, StreamStateStore streams, IOptionsMonitor<CamerasOptions> cameras, IOptions<ServerOptions> server, SdRemuxer remuxer, BackfillStatus status, TimeProvider clock, ILogger<SdBackfill> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxFileSkew = TimeSpan.FromHours(1);
    public const int MaxGapsPerRun = 20;
    public void Trigger(string cameraId);                                   // marks the camera pending and wakes the loop
    public Task RunOnceAsync(string cameraId, CancellationToken ct);         // all lenses of one xm camera
    internal Task<int> FillGapAsync(CameraConfig cam, LensConfig lens, Gap gap, DvripClient client, CancellationToken ct);   // segments added
}
```

Loop: `ExecuteAsync` subscribes `streams.StatusChanged` (`prev == Offline && Status == Online` → `Trigger(key.CameraId)`) and `agents.Connected` (→ `Trigger`), then every `Interval` runs every xm camera whose agent is connected, and immediately runs triggered ones. `RunOnceAsync`: `client = await agent.CreateDownloadClientAsync` (disposed at the end); per lens: rows `(StartUtc, DurationMs)` of the lens with `StartUtc ≥ now − SdLoopDays` → `GapFinder.Find(rows, now − SdLoopDays, now − 2 min, SdMinGapSeconds)` minus gaps already recorded as `no-files` (in-memory `HashSet<(StreamKey, Gap)>` `// ponytail: lost on restart, so a no-files gap is queried once more after a restart`), first `MaxGapsPerRun` → `FillGapAsync` each, `status.Current/Queued` updated as it goes, per-gap exceptions → `LastError` and continue.

`FillGapAsync`: `tz` conversions; `files = QueryFilesAsync(lens.DvripChannel, ToLocal(gap.From − 60 s), ToLocal(gap.To + 60 s))`; none → record `no-files`, `GapsNoFiles++`, return 0. Pre-load live rows of the lens overlapping `[gap.From − 60 s, gap.To + 60 s]`. For each file ordered by `BeginLocal`: `tmp = {DataDir}/tmp/sd/{cam}/{lens}/{Guid:N}` (created, deleted in `finally`); `DownloadAsync(file, new XmStripStream(parser, File.Create(tmp/in.264)))`; `fileStartUtc = ToUtc(parser.FirstIFrame?.CameraTime ?? file.BeginLocal)`; `fps = parser.FirstIFrame?.Fps is > 0 and <= 60 ? it : 15`; when `fileStartUtc < gap.From − MaxFileSkew || fileStartUtc > gap.To + MaxFileSkew` → warn and skip the file (Review Focus 4); `segments = RemuxAsync(tmp/in.264, fps, tmp)`; for each segment: `startUtc = fileStartUtc + seg.Start`, `durationMs = round((seg.End − seg.Start).TotalMilliseconds)`; skip when `startUtc + duration <= gap.From` or `startUtc >= gap.To`, or it overlaps a pre-loaded live row, or the target file exists (Review Focus 5); target `{recDir}/{startUtc:yyyyMMdd}/{startUtc:HH}/{startUtc:HHmmss}.ts` where `recDir = streams.TryGet(key)?.RecDir` or `NormalizePath({DataDir}/rec/{cam}/{lens})`; `File.Move` then **immediately** insert `SegmentRow { Source = "sd", Discontinuity = (first kept segment of this file), Bytes = length, Path = SegmentIndexer.RelativePath(DataDir, target) }` (`DbUpdateException` → delete the moved file, log Debug); `SegmentsAdded++`; `FilesDone++`.

`/api/status` adds `"backfill": [{ "camera", "queued", "current", "filesDone", "segmentsAdded", "gapsNoFiles", "lastError", "lastRunUtc" }]`; `status.html` panel «Довантаження з microSD» (one line per camera; «черга: N», «зараз: …», «файлів: N, сегментів: N, без записів: N», last error in `.warn`).

- [ ] **Step 1: Write the failing tests**

`GapFinderTests` (`T = 2026-07-01T12:00:00Z`, segments of 10 000 ms):
- `Contiguous_segments_have_no_gaps`; `Hole_longer_than_min_gap_is_reported` (segments 12:00–12:05 and 12:10–12:30, window 12:00–12:30 → `[12:05, 12:10)`); `Short_hole_is_ignored` (30 s hole, minGap 120 s); `Leading_and_trailing_holes_use_the_window` (window 11:50–12:40 → gaps `[12:30, 12:40)`, `[11:50, 12:00)` — newest first); `Empty_index_is_one_gap`.
`SdBackfillTests` (`[FfmpegFact]`; `FakeXmCamera`; `ApiFactory().WithoutHostedServices()`; `FakeTimeProvider` at `2026-07-01T12:30:00Z`, `TimeZone Europe/Kyiv` (UTC+3 in July); camera `c1` with lens `ptz` ch 2 (DVRIP channel 1); the agent's `CreateDownloadClientAsync` is exercised by registering a connected `XmCameraAgent` in the store):
- `Gap_is_filled_from_sd_with_true_start_and_source_sd` — seed live rows 12:00:00–12:05:00 and 12:10:00–12:30:00 UTC; fake file on channel 1 `BeginLocal 2026-07-01 15:05:00`, `EndLocal 15:05:25`, bytes = the 25-s test stream from Task 8 wrapped with I-frame time `15:05:00`, fps 15 → `RunOnceAsync("c1")` → three rows with `Source "sd"`, `StartUtc` `12:05:00`, `12:05:10`, `12:05:20`, `DurationMs` ≈ 10 000, 10 000, 5 000, first `Discontinuity true`, files exist at `rec/c1/ptz/20260701/12/120500.ts` …, `status.SegmentsAdded == 3`, `FilesDone == 1`, temp dir removed.
- `Segments_overlapping_live_rows_are_skipped` — same but the live row 12:05:00–12:05:10 also exists → only `12:05:10` and `12:05:20` added — Review Focus 5.
- `File_with_timestamp_outside_gap_is_skipped` — I-frame time `2000-01-01 00:00:00` → no rows, `LastError` null, a Warning logged (or `FilesDone 0`) — Review Focus 4.
- `Gap_without_files_is_marked_and_not_retried` — no files → `GapsNoFiles 1`; second `RunOnceAsync` → `fake.FileQueries` unchanged.
- `Offline_to_online_marks_camera_pending` — raise `StatusChanged` through `store.SetStatus(state, Offline)` then `Online` → the service's pending set contains `c1` (expose `internal IReadOnlySet<string> Pending`).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~Backfill"`
Expected: FAIL.

- [ ] **Step 3: Implement**

Register in `Program.cs`: `AddSingleton<BackfillStatus>()`, `AddSingleton<SdRemuxer>()`, `AddSingleton<SdBackfill>()` + `AddHostedService(sp => sp.GetRequiredService<SdBackfill>())`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CameraMonitor.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(backfill): fill archive gaps from the cameras' microSD over DVRIP, status panel"
```

---

### Task 10: `Fmp4BoxReader`

**Files:**
- Create: `src/CameraMonitor.Api/Features/Streams/Fmp4BoxReader.cs`
- Test: `tests/CameraMonitor.Tests/Streams/Fmp4BoxReaderTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record Fmp4Fragment(byte[] Data, bool Keyframe);   // one moof+mdat pair (plus any box between them)
public sealed class Fmp4BoxReader(Action<byte[]> onInit, Action<Fmp4Fragment> onFragment)
{
    public byte[]? Init { get; }                                   // every box before the first moof (ftyp + moov), emitted once when the first moof header is seen
    public void Feed(ReadOnlySpan<byte> data);                     // buffers partial boxes; box size 0 or 1 (largesize) → InvalidDataException; boxes outside moof..mdat after init (mfra, sidx, free) are dropped
    public static bool StartsWithKeyframe(ReadOnlySpan<byte> mdatPayload);   // AVCC walk (4-byte BE length + NAL): nal_unit_type 5 → true; 1 → false; others skipped; end → false  // ponytail: H.264 only, the cameras are H.264
    public static string? CodecString(ReadOnlySpan<byte> init);   // "avc1." + hex(profile, compat, level) from the bytes after the first "avcC" type tag; null when absent
}
```

Box = 4-byte big-endian size (including the 8-byte header) + 4-char type.

- [ ] **Step 1: Write the failing tests**

Helper `Box(string type, params byte[] payload)`. `Fmp4BoxReaderTests`:
- `Feed_in_one_byte_chunks_yields_init_once_and_fragments` — `ftyp(…)`, `moov(…)`, `moof(A)`, `mdat(IDR sample)`, `moof(B)`, `mdat(P sample)` fed byte by byte → `onInit` once with `ftyp+moov` bytes; two fragments equal to `moof+mdat` bytes; first `Keyframe true`, second `false`.
- `StartsWithKeyframe_walks_avcc_nals` — `[Theory]`: AUD then IDR (`00 00 00 02 09 F0 | 00 00 00 03 65 88 84`) → true; non-IDR slice (`00 00 00 03 41 9A 00`) → false; SEI only → false.
- `Boxes_between_fragments_are_dropped` — `… mdat, mfra(…), moof, mdat` → still two fragments, `mfra` in neither.
- `Largesize_box_is_rejected` — size field `1` → `InvalidDataException`.
- `CodecString_reads_avcC` — init containing `avcC` with `01 64 00 1F …` → `"avc1.64001f"`; init without → null.
- `[FfmpegFact] Real_ffmpeg_fmp4_is_parsed` — run ffmpeg `-f lavfi -i testsrc2=size=320x240:rate=15 -t 3 -c:v libx264 -preset veryfast -tune zerolatency -g 15 -bf 0 -pix_fmt yuv420p -f mp4 -movflags frag_keyframe+empty_moov+default_base_moof -frag_duration 200000 -min_frag_duration 100000 pipe:1` (`ProcessRunner.RunAsync` byte variant) and feed the bytes → `Init` non-null, ≥ 10 fragments, first fragment `Keyframe true`, `CodecString(Init)` starts with `"avc1."`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~Fmp4"`
Expected: FAIL.

- [ ] **Step 3: Implement**

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CameraMonitor.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(streams): fragmented-MP4 box reader with init segment, keyframe detection and codec string"
```

---

### Task 11: `LowLatencyRelay` and the `/ws/live/{cam}/{lens}` WebSocket endpoint

**Files:**
- Create: `src/CameraMonitor.Api/Features/Streams/LowLatencyRelay.cs`
- Modify: `Features/Streams/FfmpegArgs.cs` (`LowLatency`), `Features/Streams/LiveEndpoints.cs` (`/ws/live`), `Features/Cameras/ServerOptions.cs` (`MaxRelays`), `Features/Status/StatusEndpoints.cs` (`relays`), `Program.cs` (`UseWebSockets`, registrations), `tests/CameraMonitor.Tests/ApiFactory.cs` (`WithRelaySource`)
- Test: `tests/CameraMonitor.Tests/Streams/LowLatencyRelayTests.cs`, `LiveWsEndpointTests.cs`, `FfmpegArgsTests.cs` (one new test)

**Interfaces:**
- Consumes: `Fmp4BoxReader`, `StreamSupervisor.BuildSpecs` (RTSP URL per key), `ProcessRunner.RunAsync(exe, args, Stream, timeout, ct)`, `RtspUrlBuilder.Redact`.
- Produces:

```csharp
public static class FfmpegArgs { /* existing */ public static IReadOnlyList<string> LowLatency(string rtspUrl); }
// exactly: -nostdin -hide_banner -loglevel warning -rtsp_transport tcp -i {rtspUrl} -map 0:v -c:v copy -an -f mp4 -movflags frag_keyframe+empty_moov+default_base_moof -frag_duration 200000 -min_frag_duration 100000 pipe:1
public sealed class ServerOptions { /* existing */ public int MaxRelays { get; set; } = 4; }
public delegate Task<int> RelaySource(IReadOnlyList<string> ffmpegArgs, Stream sink, CancellationToken ct);   // default: ProcessRunner.RunAsync(server.FfmpegPath, args, sink, Timeout.InfiniteTimeSpan, ct)
public sealed class RelaySubscription(ChannelReader<byte[]> messages, Action unsubscribe) : IDisposable { public ChannelReader<byte[]> Messages { get; } public void Dispose(); }
public sealed class LowLatencyRelay(IOptionsMonitor<CamerasOptions> cameras, IOptions<ServerOptions> server, RelaySource source, TimeProvider clock, ILogger<LowLatencyRelay> logger)
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    public const int ClientQueue = 64;                      // messages buffered per client; a client this far behind is dropped
    public int ActiveRelays { get; }
    public IReadOnlyList<StreamKey> ActiveKeys { get; }
    public RelaySubscription? Subscribe(StreamKey key);     // null: unknown lens (no spec) or ActiveRelays >= MaxRelays with no relay for this key yet
}
```

Relay per key (`// ponytail: one lock per relay`): started on the first subscriber with `source(FfmpegArgs.LowLatency(spec.RtspUrl), new BoxFeedStream(reader), cts.Token)`; `reader = new Fmp4BoxReader(onInit, onFragment)` where `onInit` stores the init and broadcasts it, `onFragment` updates the GOP backlog (keyframe → clear then add; else add; > 300 fragments → clear) and broadcasts. Broadcast = `TryWrite` on each client's bounded `Channel<byte[]>(ClientQueue)` (`FullMode = Wait`, write via `TryWrite`); a failed `TryWrite` removes the client and completes its channel (Review Focus 3). A new subscriber whose relay already has an init receives init + the current GOP backlog before live fragments. Source exit (any code or exception, logged with `Redact`) → complete all clients, remove the relay. Last client gone → `Task.Delay(IdleTimeout, clock, idleCts.Token)`; still no client → cancel the source, remove the relay; a new subscriber cancels the idle timer.

Endpoint `GET /ws/live/{cam}/{lens}`: not a WebSocket request → 400; unsafe names → 400; `Subscribe` null because the lens is unknown → 404; null because of the limit → accept, then close with `(WebSocketCloseStatus)1013` «relay limit»; else accept and forward every message as one binary frame; a concurrent `ReceiveAsync` loop ends the session when the client closes; when the reader completes (source ended or client dropped) close with `1011` «stream ended»; `finally` dispose the subscription. `Program.cs`: `app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) })` before the endpoints; `/ws` joins the Debug request-log list. `/api/status` adds `"relays": { "active", "max", "keys": ["cam/lens"] }`.

`ApiFactory.WithRelaySource(RelaySource)` replaces the delegate (default stays the ffmpeg one).

- [ ] **Step 1: Write the failing tests**

`FfmpegArgsTests.LowLatency_builds_fmp4_pipe_args` — exact list above for `rtsp://x/y`.
`LowLatencyRelayTests` (fake source: writes `init` bytes, then every 20 ms a fragment from a scripted list of `(bytes, keyframe)` built with the Task 10 `Box` helper, until `ct`; records `Started`/`Cancelled`; cameras = one xm camera `c1` with lens `a`, `MaxRelays 4`, `FakeTimeProvider`):
- `First_subscriber_starts_source_and_receives_init_then_fragments` — first message == init, then fragments in order.
- `Late_subscriber_gets_init_and_gop_backlog_first` — script: key, p, p (then pause) → subscriber 2 joins → its first four messages are init, key, p, p.
- `Source_stops_30s_after_last_unsubscribe_and_resubscribe_cancels_the_timer` — dispose → `Advance(29 s)` → source still running; `Advance(2 s)` → cancelled; and: dispose → `Advance(10 s)` → subscribe → `Advance(30 s)` → still running.
- `Fifth_relay_is_refused_but_second_viewer_of_an_existing_relay_is_not` — five lenses configured, `MaxRelays 4` → 5th `Subscribe` null; a second `Subscribe` on lens 1 non-null; `ActiveRelays == 4`.
- `Slow_client_is_dropped_not_blocking_others` — client A never reads; after 70 fragments A's `Messages.Completion` is completed, B has received all 70 — Review Focus 3.
- `Unknown_lens_returns_null`.
- `Source_exit_completes_all_clients_and_frees_the_slot` — fake source returns `1` after 3 fragments → both readers complete, `ActiveRelays == 0`.
`LiveWsEndpointTests` (`ApiFactory().WithRelaySource(fake).WithCameras(sim1/main rtsp)`; `factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws/live/sim1/main"), ct)`):
- `Ws_streams_init_then_fragments` — first binary message == init.
- `Ws_unknown_lens_is_404` — `ConnectAsync` throws (status 404).
- `Ws_over_limit_closes_with_1013` — `WithSetting("Server:MaxRelays", "0")` → `ReceiveAsync` returns `Close` with `CloseStatus == 1013`.
- `Ws_rejects_traversal_names` — `/ws/live/a/..%2Fb` → 400.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~Relay|FullyQualifiedName~LiveWs|FullyQualifiedName~FfmpegArgs"`
Expected: FAIL.

- [ ] **Step 3: Implement**

- [ ] **Step 4: Run tests; manual check**

Run: `dotnet test tests/CameraMonitor.Tests` → PASS. With the simulator running: `websocat -b ws://localhost:8080/ws/live/sim1/main | head -c 100000 | wc -c` (or a 10-line browser console script) receives data continuously; `/api/status` shows `relays.active 1`; stop the client → `active 0` after ~30 s and the ffmpeg process is gone.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(streams): on-demand fMP4 relay over WebSocket with GOP backlog, idle stop and relay limit"
```

---

### Task 12: MSE low-latency player in `cam.html` with HLS fallback, 24-h timeline

**Files:**
- Modify: `src/CameraMonitor.Api/wwwroot/cam.html`, `wwwroot/app.css`

**Interfaces:**
- Consumes: `/ws/live/{cam}/{lens}` (binary: init first, then fragments; close 1013 = relay limit), `/api/timeline` (`ranges[].source`, `events[]`), `startHls(note)`/`setStatus` from Task 1.

Player (`startMse()`): require `window.MediaSource && MediaSource.isTypeSupported('video/mp4; codecs="avc1.42E01E"')`, else `startHls('HLS (резерв)')`. `ms = new MediaSource()`, `video.src = URL.createObjectURL(ms)`; on `sourceopen` open `new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/ws/live/…')` with `binaryType = 'arraybuffer'`. First message: `codec = codecFromInit(u8) ?? 'avc1.42E01E'` (find the ASCII `avcC` tag, take the three bytes at offsets +5, +6, +7 as hex), `sb = ms.addSourceBuffer('video/mp4; codecs="' + codec + '"')`, `sb.mode = 'sequence'`; every message goes to a queue drained on `updateend` (`appendBuffer` only when `!sb.updating`). After each `updateend`: `const end = video.buffered.length ? video.buffered.end(video.buffered.length - 1) : 0; if (end - video.currentTime > 1) video.currentTime = end - 0.3;` (`// ponytail: ~0.3 s target buffer`); once per 30 s `sb.remove(0, video.currentTime - 10)` when the buffer is longer than 20 s; `video.play()` after the first fragment; `setStatus('Низька затримка')`. `ws.onclose`: code `1013` → `startHls('Забагато глядачів з низькою затримкою, показую HLS')`; otherwise `setStatus('Перепідключення…')` and restart `startMse()` after 3 s (tear down `ws`, `sb`, `ms`). `ws.onerror` → same as close. The `?player=hls` query forces `startHls()` (debugging).

Timeline («Останні 24 год»): a `.timeline` strip like `archive.html` but spanning `[now − 24 h, now]`; fetch `/api/timeline` for the two UTC days covering it, draw ranges (`.range`, `.range.sd`) and event ticks; click → `archive.html?cam=&lens=&t=`; refresh every 60 s. CSS for `.range.sd` shared with `archive.html` (move to `app.css`).

- [ ] **Step 1: Implement the player and the timeline**

- [ ] **Step 2: Manual check on the simulator**

`dotnet run`, open `/cam.html?cam=sim1&lens=main`: status «Низька затримка», the overlay clock in the picture vs the system clock ≤ 1 s; `/api/status` `relays.active 1`; close the tab → `active 0` after 30 s; `docker compose … stop cam1` → «Перепідключення…», `start cam1` → playback resumes; `/cam.html?cam=sim1&lens=main&player=hls` plays through hls.js; `Server:MaxRelays=0` → page falls back to HLS with the «Забагато глядачів…» note; the 24-h strip shows the recorded ranges and opens the archive at the clicked time.

- [ ] **Step 3: Run all tests, format**

Run: `dotnet format CameraMonitor.sln --verify-no-changes && dotnet test tests/CameraMonitor.Tests` → PASS.

- [ ] **Step 4: Commit**

```bash
git add src/CameraMonitor.Api/wwwroot
git commit -m "feat(web): MSE low-latency player with HLS fallback and a 24-hour timeline on the camera page"
```

---

### Task 13: Documentation, deferred rulings, real-camera verification

**Files:**
- Modify: `docs/SETUP.md`, `README.md`, `deploy/cameras.example.json` (`"DvripPort": 34567` on the example camera), `deploy/docker-compose.yml` (comment: open `XmAlarmServerPort` only on the host network, which is already the case)
- Create: `docs/superpowers/plans/2026-10-26-camera-monitor-plan-b-deferred.md`

- [ ] **Step 1: Documentation (Ukrainian)**

`docs/SETUP.md`: new section «DVRIP (Xiongmai)»: port 34567 must be reachable; what the agent changes on every camera at connect (GOP = 1 с, smart-encode off, детекція руху/людини ввімкнена, час = серверний у `Server:TimeZone`, щодоби); `LensConfig.XmChannel` override; «Тривоги не приходять?» → set `Server:XmAlarmServerPort` (15002) and `Server:XmAlarmServerHost` (the server's LAN IP) in `docker-compose.yml` environment; `Server:TileSource`; «Довантаження з microSD»: картка потрібна в кожній камері, `Server:SdLoopDays` ≈ period of the card, status panel; «Живе відео вибраної камери»: `/ws/live`, `Server:MaxRelays`, браузери без MSE отримують HLS. Remove the delivered items from «Буде в наступних планах» (keep ONVIF, Telegram, login). `README.md`: link to this plan, list pages («Камери · Архів · Події · Статус»).

- [ ] **Step 2: Deferred/rulings file**

`docs/superpowers/plans/2026-10-26-camera-monitor-plan-b-deferred.md`: every minor finding the task reviewers deferred, every controller ruling made during execution (format of the Plan A deferred file), the measured tile timing from Step 3 and the PTZ preset-constant outcome.

- [ ] **Step 3: Real-camera verification (needs the camera on the developer's desk)**

Preconditions: camera LAN address in `deploy/cameras.json` (copied from the example, `Vendor "xm"`, lenses `fixed` ch 1 / `ptz` ch 2), password in `deploy/secrets/cam_password` (or `Cameras:DefaultPasswordFile` pointing at it in `appsettings.Development.json`); run `dotnet run --project src/CameraMonitor.Api` with `Server:CamerasFile` pointing at that file. Checks, each recorded in the deferred file:
1. `/status.html` «Камери (DVRIP)» shows «підключено», a clock drift, GOP 1; the log shows the `Video` block; `ffprobe -rtsp_transport tcp -show_frames -select_streams v -show_entries frame=key_frame -of csv <main rtsp url> | head -60` → a key frame every ≈ fps frames.
2. PTZ pad on `/cam.html?cam=…&lens=ptz` moves the PTZ lens while pressed and stops within 0.5 s of release; if it keeps moving on release, swap `PtzStartPreset`/`PtzStopPreset` and re-test; if `fixed` moves instead, set `XmChannel` and record the mapping. Preset «Зберегти» 1 → move → «Перейти» 1 returns.
3. Wave in front of each lens: an event with a thumbnail appears on `/events.html` within 5 s. If nothing within 2 min: set `Server:XmAlarmServerPort=15002`, `Server:XmAlarmServerHost=<server LAN IP>`, restart, re-test; record which path works.
4. Tiles: `curl -o /dev/null -s -w "%{time_total} %{size_download}\n"` ×5 for `/api/tile/{cam}/fixed.jpg` with `TileSource ffmpeg` and `dvrip` → record both; set the default in `appsettings.json` only if dvrip is faster **and** ≤ 100 KB, else keep ffmpeg.
5. M3b: unplug the camera's network cable for 5 min, plug it back: within ~10 min `/archive.html` shows the gap in the microSD colour and playback across it works; the backfill panel shows the counts.
6. M4: `/cam.html` with a clock in the frame → glass-to-glass ≤ 2 s; a PTZ tap shows in the picture without overshoot; close the page → `relays.active 0` after 30 s.

- [ ] **Step 4: Final checks and commit**

Run: `dotnet format CameraMonitor.sln --verify-no-changes && dotnet test tests/CameraMonitor.Tests` → PASS.

```bash
git add -A
git commit -m "docs: Plan B setup notes (DVRIP, microSD backfill, low-latency live), deferred rulings"
```

---

## Self-review notes

- Spec coverage: M3 — DVRIP port (T2–T3), agent with login/keepalive/alarms/snapshot/PTZ/time/get-set config, `events` table, `events.html`, PTZ pad + presets, timeline events, snapshot-based tiles behind a measured switch (T4–T5), alarm-push fallback (T6), fixture tests for framing and `sofia_hash` (T2). M3b — `Source` migration (T7), gap detection, `OPFileQuery` + download + remux + self-indexing before `DirectoryScanner.MinAge`, loop-period limit, coloured timeline, status queue (T8–T9). M4 — relay, box reader, WebSocket endpoint, MSE player with HLS fallback, relay limit, 30-s idle stop, late-join behaviour (T10–T12). Parked Plan A item (T1). Verification on the real camera (T13). Not in this plan by design: ONVIF, Telegram alerts, cookie auth (Plan C); H.265 keyframe detection (cameras are H.264).
- Types used across tasks: `DvripCodec`/`DvripFrame` (T2) → T3, T6; `DvripClient`, `AlarmInfo`, `XmFileEntry`, `PtzStartPreset/PtzStopPreset` (T3) → T4, T5, T9; `XmAgentStore`, `XmCameraAgent.HandleAlarmAsync/SnapshotAsync/PtzAsync/CreateDownloadClientAsync`, `LensConfig.DvripChannel` (T4) → T5, T6, T9; `SegmentRow.Source` (T7) → T9; `XmFrameParser`/`XmStripStream`, `SdRemuxer.RemuxedSegment` (T8) → T9; `Fmp4BoxReader`/`Fmp4Fragment` (T10) → T11; `RelaySource`, `RelaySubscription` (T11) → T12.
- Review Focus pinned: 1 → T2/T3, 2 → T2, 3 → T11, 4 → T9, 5 → T9.
