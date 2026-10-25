# CameraMonitor Core (Plan A: M0–M2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A .NET 9 service that pulls every lens of every configured camera over RTSP exactly once, records 10-second MPEG-TS segments to disk with an SQLite index, serves live HLS and a JPEG tile grid in the browser, plays back and exports the archive, and enforces retention — the NVR core that Plan B (DVRIP/PTZ/low-latency) and Plan C (ONVIF/alerts/hardening) build on.

**Architecture:** One ASP.NET Core process. `StreamSupervisor` runs one `StreamWorker` per lens; each worker owns one ffmpeg process whose `tee` output writes archive segments (HLS muxer, 10 s, `strftime_mkdir`) and a live HLS playlist (2 s) without re-encoding, and is restarted with capped backoff when `-progress` stalls. `SegmentIndexer` polls each stream's `archive.m3u8` for completed segments and writes rows to SQLite via EF Core; `DirectoryScanner` catches orphan files left by killed processes. Minimal API endpoints synthesize VOD playlists from the index; static HTML + hls.js is the UI. `RetentionService` deletes by age or free-space threshold.

**Tech Stack:** .NET 9 (ASP.NET Core minimal API, `BackgroundService`, `TimeProvider`), Serilog, EF Core 9 + SQLite, ffmpeg/ffprobe (external binaries), hls.js (vendored), xUnit + FluentAssertions 6.12 + NSubstitute + `Microsoft.Extensions.TimeProvider.Testing`, Docker Compose, mediamtx (dev-only camera simulator).

**Spec:** `docs/superpowers/specs/2026-10-25-camera-monitor-design.md` (Ukrainian). This plan covers milestones M0, M1, M2 of that spec. Plans B and C follow after this one ships.

## Global Constraints

- `net9.0`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `sealed` classes with primary constructors, file-scoped namespaces (team convention from TTApp/Oberig).
- Video is never re-encoded on the server: every ffmpeg command uses `-c:v copy`. Audio is dropped (`-an`) unless `Server:RecordAudio=true`, in which case it is transcoded to AAC (MPEG-TS cannot carry the cameras' G.711).
- Each camera stream is pulled from the camera exactly once, by the supervisor; tiles and archive playback read from local files.
- Paths handed to ffmpeg use forward slashes and must not contain `:` (the `tee` option syntax uses `:` as a separator). Linux absolute paths are fine; on Windows dev use relative `Server:DataDir=./data`, `Server:LiveDir=./data/live`.
- Camera passwords and bot tokens never appear in logs or API responses: log RTSP URLs through `RtspUrlBuilder.Redact`.
- All user-facing UI strings are Ukrainian; dark theme.
- Layout: `src/CameraMonitor.Api` (vertical slices under `Features/`, shared plumbing under `Infrastructure/`), `tests/CameraMonitor.Tests`. Root namespace `CameraMonitor.Api`.
- Serilog with `ReadFrom.Configuration`, compact JSON console sink. Options bound with `AddOptions<T>().BindConfiguration(...).ValidateOnStart()`.
- `BackgroundService.ExecuteAsync` never lets an exception escape (it would stop the host): loop body wrapped in try/catch with `catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }`.
- Conventional Commits (`feat(streams): ...`, `test: ...`, `chore: ...`), `dotnet format` clean, `dotnet test` green before every commit. `.editorconfig`: 4 spaces, LF, final newline.
- Default ports/paths from the spec: Kestrel 8080; `/data` (archive + db), `/run/live` (tmpfs), `Server:SegmentSeconds=10`, `Server:HlsSeconds=2`, `Server:RetentionDays=14`, `Server:MinFreeGb=100`, `Server:MinRetentionDays=3`.

## Review Focus

1. **Camera password containing `&`, `?`, `#`, `/`, `:`, `@` or a space** — the Xiongmai RTSP template embeds it raw, so the stream would silently fail to authenticate; expected: startup validation error naming the camera. Pinned in Task 2.
2. **`Server:DataDir` or `Server:LiveDir` with a `:` (Windows absolute path)** — ffmpeg's tee spec would be mis-parsed and every stream would crash-loop; expected: clear `ArgumentException` at startup pointing at the relative-path rule. Pinned in Task 3.
3. **Path traversal in `/live/{cam}/{lens}/{file}` and `/seg/...`** (`..`, `%2e%2e`, backslash) — expected: 400, never a file outside the stream directory. Pinned in Tasks 5 and 8.
4. **Archive request with `from >= to`, a range over 24 h, or an export over 2 h** — expected: 400 with a message, not a multi-gigabyte playlist or an endless ffmpeg. Pinned in Task 8.
5. **Partial `.ts` left behind when ffmpeg is killed mid-segment** — it is never listed in `archive.m3u8`; expected: it still appears in the archive (indexed by `DirectoryScanner` with a probed duration). Pinned in Task 7.

---

### Task 1: Repository skeleton, tooling, dev camera simulator

**Files:**
- Create: `CameraMonitor.sln`, `src/CameraMonitor.Api/CameraMonitor.Api.csproj`, `src/CameraMonitor.Api/Program.cs`, `src/CameraMonitor.Api/appsettings.json`, `src/CameraMonitor.Api/appsettings.Development.json`
- Create: `tests/CameraMonitor.Tests/CameraMonitor.Tests.csproj`, `tests/CameraMonitor.Tests/ApiFactory.cs`, `tests/CameraMonitor.Tests/HealthTests.cs`
- Create: `.editorconfig` (copy `D:\Work\TTApp\TTApp_Backend\.editorconfig`, set `root = true`), `.gitattributes` (`* text=auto eol=lf`), `.gitignore` (`dotnet new gitignore` + `data/`, `secrets/`), `.githooks/pre-commit`, `.githooks/pre-push`, `.github/workflows/ci.yml`, `README.md`
- Create: `deploy/compose.dev.yml`

**Interfaces:**
- Produces: `public partial class Program { }` at the bottom of `Program.cs` (needed by `WebApplicationFactory<Program>`); `ApiFactory : WebApplicationFactory<Program>` that sets `Server:DataDir` and `Server:LiveDir` to a fresh temp directory per instance and `Server:CamerasFile` to an empty-cameras JSON it writes there (later tasks override `FfmpegFactory` and seed cameras through `ApiFactory.WithCameras(...)` / `ApiFactory.WithFfmpeg(...)` builder methods added in Tasks 4 and 5).

- [ ] **Step 1: Create solution and projects**

```bash
cd D:/Work/CameraMonitor
dotnet new sln -n CameraMonitor
dotnet new web -o src/CameraMonitor.Api -n CameraMonitor.Api --framework net9.0
dotnet new xunit -o tests/CameraMonitor.Tests -n CameraMonitor.Tests --framework net9.0
dotnet sln add src/CameraMonitor.Api tests/CameraMonitor.Tests
dotnet add tests/CameraMonitor.Tests reference src/CameraMonitor.Api
```

Packages — Api: `Serilog.AspNetCore` 9.0.*, `Serilog.Formatting.Compact` 3.0.*. Tests: `xunit` 2.9.2, `xunit.runner.visualstudio` 2.8.2, `Microsoft.NET.Test.Sdk` 17.12.0, `FluentAssertions` 6.12.2, `NSubstitute` 5.1.0, `coverlet.collector` 6.0.2, `Microsoft.AspNetCore.Mvc.Testing` 9.0.*, `Microsoft.Extensions.TimeProvider.Testing` 9.*. Add `<Using Include="Xunit" />` and `<Using Include="FluentAssertions" />` to the test csproj.

- [ ] **Step 2: Write the failing smoke test**

```csharp
public sealed class HealthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_returns_200_Healthy()
    {
        var resp = await factory.CreateClient().GetAsync("/health");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }
}
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test tests/CameraMonitor.Tests`
Expected: FAIL (compile error: `Program` not accessible / `/health` 404).

- [ ] **Step 4: Implement `Program.cs`**

Serilog bootstrap (`builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration))`, `appsettings.json` configures the `Console` sink with `Serilog.Formatting.Compact.RenderedCompactJsonFormatter, Serilog.Formatting.Compact`), `AddHealthChecks()`, `MapHealthChecks("/health")`, `UseSerilogRequestLogging()`, `UseDefaultFiles()` + `UseStaticFiles()`, `public partial class Program { }`. `appsettings.Development.json` uses the plain console sink.

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/CameraMonitor.Tests`
Expected: PASS (1 test).

- [ ] **Step 6: Tooling files**

`.githooks/pre-commit`: if any staged `*.cs`, run `dotnet format CameraMonitor.sln --verify-no-changes`; `.githooks/pre-push`: `dotnet build -c Release` then `dotnet test -c Release --no-build`; both honour `SKIP_HOOKS=1`. Enable with `git config core.hooksPath .githooks`. `ci.yml`: ubuntu-latest, `actions/setup-dotnet` 9.0.x, format verify, build Release, test. `README.md`: what it is (one paragraph, Ukrainian), how to run dev (Step 7 commands), link to spec and plan.

- [ ] **Step 7: Dev camera simulator `deploy/compose.dev.yml`**

Services: `mediamtx` (image `bluenviron/mediamtx`, pin the current 1.x tag, `ports: ["8554:8554"]`); `cam1` and `cam2` (image `linuxserver/ffmpeg`, `restart: unless-stopped`, `depends_on: [mediamtx]`), command:

```
-re -stream_loop -1 -f lavfi -i testsrc2=size=1280x720:rate=15 -vf drawtext=text='cam1 %{localtime}':fontsize=42:x=24:y=24:fontcolor=white:box=1:boxcolor=black@0.5 -c:v libx264 -preset veryfast -tune zerolatency -g 15 -bf 0 -pix_fmt yuv420p -f rtsp rtsp://mediamtx:8554/cam1
```

(`cam2` identical with `cam2`.) Verify: `docker compose -f deploy/compose.dev.yml up -d` then `ffprobe -rtsp_transport tcp rtsp://localhost:8554/cam1` prints an h264 1280x720 stream.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "chore: solution skeleton, Serilog, health check, hooks, CI, dev camera simulator"
```

---

### Task 2: Options, camera config file, RTSP URL builder

**Files:**
- Create: `src/CameraMonitor.Api/Features/Cameras/ServerOptions.cs`, `CamerasOptions.cs`, `CamerasOptionsValidator.cs`, `CameraPasswords.cs`, `RtspUrlBuilder.cs`
- Create: `src/CameraMonitor.Api/Features/Streams/StreamKey.cs`
- Create: `deploy/cameras.example.json`, `deploy/cameras.dev.json`
- Modify: `src/CameraMonitor.Api/Program.cs` (options registration, cameras file)
- Test: `tests/CameraMonitor.Tests/Cameras/CamerasOptionsValidatorTests.cs`, `RtspUrlBuilderTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed class ServerOptions
{
    public const string Section = "Server";
    public string DataDir { get; set; } = "/data";
    public string LiveDir { get; set; } = "/run/live";
    public string CamerasFile { get; set; } = "cameras.json";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public int SegmentSeconds { get; set; } = 10;
    public int HlsSeconds { get; set; } = 2;
    public int RetentionDays { get; set; } = 14;
    public int MinFreeGb { get; set; } = 100;
    public int MinRetentionDays { get; set; } = 3;
    public bool RecordAudio { get; set; } = false;
    public string TimeZone { get; set; } = "Europe/Kyiv";
}
public sealed class CamerasOptions { public const string Section = "Cameras"; public string? DefaultPassword { get; set; } public string? DefaultPasswordFile { get; set; } public List<CameraConfig> Items { get; set; } = []; }
public sealed class CameraConfig { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Host { get; set; } = ""; public string User { get; set; } = "admin"; public string? Password { get; set; } public string? PasswordFile { get; set; } public string Vendor { get; set; } = "xm"; /* "xm" | "rtsp" */ public List<LensConfig> Lenses { get; set; } = []; }
public sealed class LensConfig { public string Id { get; set; } = ""; public int Channel { get; set; } = 1; public bool Ptz { get; set; } public string? RtspUrl { get; set; } }
public readonly record struct StreamKey(string CameraId, string LensId) { public override string ToString() => $"{CameraId}/{LensId}"; }
public static class CameraPasswords { public static string Resolve(CamerasOptions opts, CameraConfig cam); } // cam.Password ?? File(cam.PasswordFile) ?? opts.DefaultPassword ?? File(opts.DefaultPasswordFile) ?? "" (file contents trimmed)
public static class RtspUrlBuilder { public static string Build(CameraConfig cam, LensConfig lens, string password); public static string Redact(string rtspUrl); }
public sealed class CamerasOptionsValidator : IValidateOptions<CamerasOptions>
```

- [ ] **Step 1: Write failing tests**

`RtspUrlBuilderTests`:
- `Build_uses_xm_template`: cam Host `192.168.50.11`, User `admin`, lens Channel 2, password `s3cret` → exactly `rtsp://192.168.50.11:554/user=admin&password=s3cret&channel=2&stream=0.sdp?real_stream`.
- `Build_prefers_lens_override`: lens `RtspUrl = "rtsp://x/y"` → `"rtsp://x/y"`.
- `Redact_hides_password_in_both_styles`: `...password=s3cret&...` → contains `password=***`, not `s3cret`; `rtsp://admin:s3cret@h/x` → `rtsp://admin:***@h/x`.

`CamerasOptionsValidatorTests` (call `Validate(null, options).Succeeded` / `.FailureMessage`):
- `Valid_config_passes` (two cameras, two lenses each).
- `Duplicate_camera_id_fails` (message contains the id).
- `Duplicate_lens_id_within_camera_fails`.
- `Camera_without_lenses_fails`.
- `Bad_id_format_fails` (`"Цех 1"`; rule `^[a-z0-9][a-z0-9-]{0,31}$`).
- `Xm_password_with_url_delimiters_fails` (password `a&b`; message names the camera and the forbidden characters `& ? # / : @` and space) — Review Focus 1.
- `Rtsp_vendor_requires_lens_url_fails`.
- `Unknown_vendor_fails`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~Cameras"`
Expected: FAIL (types missing).

- [ ] **Step 3: Implement the types listed in Interfaces**

Validator resolves each camera's password via `CameraPasswords.Resolve` to check the delimiter rule for vendor `xm`. In `Program.cs`: read `ServerOptions` early (`builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new()`), then `builder.Configuration.AddJsonFile(Path.GetFullPath(server.CamerasFile), optional: false, reloadOnChange: true)`; register `AddOptions<ServerOptions>().BindConfiguration(ServerOptions.Section).ValidateOnStart()`, `AddOptions<CamerasOptions>().BindConfiguration(CamerasOptions.Section).ValidateOnStart()`, `AddSingleton<IValidateOptions<CamerasOptions>, CamerasOptionsValidator>()`, `AddSingleton(TimeProvider.System)`. Validation failure message must list every problem, one per line.

`deploy/cameras.example.json`: one XM camera `cex-gate` ("Цех, ворота", `192.168.50.11`, lenses `fixed` ch 1, `ptz` ch 2 `Ptz: true`), `DefaultPasswordFile: "/run/secrets/cam_password"`. `deploy/cameras.dev.json`: cameras `sim1`, `sim2`, Vendor `rtsp`, one lens `main` each with `RtspUrl` `rtsp://localhost:8554/cam1` / `cam2`. `appsettings.Development.json`: `Server:DataDir=./data`, `Server:LiveDir=./data/live`, `Server:CamerasFile=../../deploy/cameras.dev.json`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CameraMonitor.Tests`
Expected: PASS (all).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(cameras): server/camera options with validation, cameras.json, RTSP URL builder"
```

---

### Task 3: ffmpeg arguments, progress parsing, process wrapper

**Files:**
- Create: `src/CameraMonitor.Api/Features/Streams/FfmpegArgs.cs`, `ProgressParser.cs`, `IFfmpegProcess.cs`, `FfmpegProcess.cs`
- Test: `tests/CameraMonitor.Tests/Streams/FfmpegArgsTests.cs`, `ProgressParserTests.cs`, `FfmpegProcessTests.cs`, `tests/CameraMonitor.Tests/FfmpegFactAttribute.cs`

**Interfaces:**
- Consumes: `ServerOptions` (Task 2).
- Produces:

```csharp
public static class FfmpegArgs
{
    public static string NormalizePath(string path);            // '\' → '/', trims trailing '/', throws ArgumentException if it contains ':'
    public static IReadOnlyList<string> Recording(string rtspUrl, string recDir, string liveDir, int segmentSeconds, int hlsSeconds, bool recordAudio);
}
public sealed class ProgressState { public TimeSpan OutTime { get; set; } public long TotalBytes { get; set; } public double Fps { get; set; } public string? Bitrate { get; set; } }
public static class ProgressParser { public static bool TryApply(string line, ProgressState state); } // returns true on "progress=..." (end of a block); parses out_time_us=, total_size=, fps=, bitrate=
public interface IFfmpegProcess : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    DateTimeOffset? LastProgressAt { get; }   // set when a progress block ends
    ProgressState Progress { get; }
    Task<int> Exited { get; }                 // exit code
    void Kill();
}
public delegate IFfmpegProcess FfmpegFactory(IReadOnlyList<string> args, string logName);
public sealed class FfmpegProcess(string ffmpegPath, IReadOnlyList<string> args, string logName, TimeProvider clock, ILogger logger) : IFfmpegProcess
```

`Recording(...)` returns exactly:

```
-nostdin -hide_banner -loglevel warning -rtsp_transport tcp -i {rtspUrl}
-map 0:v -c:v copy            (+ when recordAudio: -map 0:a? -c:a aac -b:a 64k ; otherwise: -an)
-progress pipe:1
-f tee
[f=hls:hls_time={segmentSeconds}:hls_segment_type=mpegts:hls_list_size=3:hls_flags=omit_endlist+program_date_time:strftime=1:strftime_mkdir=1:hls_segment_filename={rec}/%Y%m%d/%H/%H%M%S.ts]{rec}/archive.m3u8|[f=hls:hls_time={hlsSeconds}:hls_list_size=6:hls_flags=delete_segments+independent_segments+program_date_time:onfail=ignore]{live}/index.m3u8
```

(each token one list element; the tee spec is a single element; `{rec}`/`{live}` are `NormalizePath`ed).

- [ ] **Step 1: Write failing tests**

`FfmpegArgsTests`: `Recording_builds_expected_tee_spec` (assert the full tee element string for rec `/data/rec/a/b`, live `/run/live/a/b`, 10, 2, false, and that `-an` is present and `-c:a` absent); `Recording_with_audio_adds_aac`; `NormalizePath_converts_backslashes`; `NormalizePath_rejects_colon` (`D:/data` → `ArgumentException` whose message contains `relative`) — Review Focus 2.
`ProgressParserTests`: `out_time_us=12500000` → `OutTime == 12.5 s`; `fps=14.98`, `bitrate=2501.3kbits/s`, `total_size=1024`; `progress=continue` returns true, other lines false; garbage line ignored.
`FfmpegProcessTests` with `[FfmpegFact]` (skips when `ffmpeg -version` cannot run): `StartAsync_reports_progress_then_exits_zero` using args `-nostdin -f lavfi -i testsrc=duration=1:size=64x64:rate=10 -progress pipe:1 -f null -`; after `await Exited` → `0`, `LastProgressAt` not null, `Progress.OutTime >= 0.9 s`. `Kill_terminates_running_process` with `testsrc=duration=60` → `Kill()` → `Exited` completes within 5 s.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~Streams"`
Expected: FAIL.

- [ ] **Step 3: Implement**

`FfmpegProcess`: `ProcessStartInfo` with `ArgumentList`, `RedirectStandardOutput/Error`, `UseShellExecute=false`, environment `TZ=UTC`; background tasks read stdout line-by-line into `ProgressParser` (set `LastProgressAt = clock.GetUtcNow()` when `TryApply` returns true) and stderr lines to `logger.LogWarning("[{Name}] {Line}", logName, line)`; `Exited` is a `TaskCompletionSource<int>` completed from `Process.Exited` (`EnableRaisingEvents`); `Kill()` → `Process.Kill(entireProcessTree: true)` guarded for already-exited. `logName` is the redacted stream name (callers pass `StreamKey.ToString()`).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CameraMonitor.Tests`
Expected: PASS (ffmpeg tests run on this dev machine; skipped where ffmpeg is absent).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(streams): ffmpeg recording args, progress parser, process wrapper"
```

---

### Task 4: Stream supervisor and `/api/cameras`

**Files:**
- Create: `src/CameraMonitor.Api/Features/Streams/Backoff.cs`, `StreamState.cs`, `StreamStateStore.cs`, `StreamSpec.cs`, `StreamWorker.cs`, `StreamSupervisor.cs`, `src/CameraMonitor.Api/Features/Cameras/CamerasEndpoints.cs`
- Modify: `Program.cs` (register `StreamStateStore` singleton, `FfmpegFactory` → `FfmpegProcess`, `AddHostedService<StreamSupervisor>()`, `app.MapCamerasEndpoints()`), `tests/CameraMonitor.Tests/ApiFactory.cs` (`WithFfmpeg(FfmpegFactory)`, `WithCameras(CamerasOptions)` that writes the cameras JSON; default factory = fake)
- Test: `tests/CameraMonitor.Tests/Streams/BackoffTests.cs`, `StreamWorkerTests.cs`, `StreamSupervisorTests.cs`, `tests/CameraMonitor.Tests/Streams/FakeFfmpegProcess.cs`, `tests/CameraMonitor.Tests/Cameras/CamerasEndpointTests.cs`

**Interfaces:**
- Consumes: `IFfmpegProcess`, `FfmpegFactory`, `FfmpegArgs.Recording`, `RtspUrlBuilder`, `CameraPasswords`, `StreamKey`.
- Produces:

```csharp
public static class Backoff { public static TimeSpan NextDelay(int attempt); } // 2^attempt seconds, attempt clamped to 0..6 → 1,2,4,8,16,32,60(cap)
public enum StreamStatus { Starting, Online, Offline }
public sealed class StreamState
{
    public required StreamKey Key { get; init; }
    public StreamStatus Status { get; set; } = StreamStatus.Starting;
    public DateTimeOffset? LastProgressAt { get; set; }
    public DateTimeOffset? OnlineSince { get; set; }
    public DateTimeOffset? OfflineSince { get; set; }
    public int Restarts { get; set; }
    public int Generation { get; set; }       // incremented per ffmpeg start
    public double Fps { get; set; }
    public string? Bitrate { get; set; }
    public string RecDir { get; set; } = "";  // absolute, normalized
    public string LiveDir { get; set; } = "";
}
public sealed class StreamStateStore
{
    public StreamState GetOrAdd(StreamKey key);
    public bool TryGet(StreamKey key, out StreamState state);
    public bool Remove(StreamKey key);
    public IReadOnlyList<StreamState> All();
    public event Action<StreamState, StreamStatus /*previous*/>? StatusChanged;   // raised by workers; Plan C's AlertEngine subscribes
    public void SetStatus(StreamState state, StreamStatus status, DateTimeOffset now);  // updates Online/OfflineSince and raises the event
}
public sealed record StreamSpec(StreamKey Key, string RtspUrl, string RecDir, string LiveDir);
public sealed class StreamWorker(StreamSpec spec, FfmpegFactory ffmpeg, StreamStateStore store, ServerOptions server, TimeProvider clock, ILogger logger)
{
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(60);
    public Task RunAsync(CancellationToken ct);
}
public sealed class StreamSupervisor(IOptionsMonitor<CamerasOptions> cameras, IOptions<ServerOptions> server, StreamStateStore store, FfmpegFactory ffmpeg, TimeProvider clock, ILoggerFactory loggers) : BackgroundService
{
    public static IReadOnlyList<StreamSpec> BuildSpecs(CamerasOptions cameras, ServerOptions server);  // RecDir = {DataDir}/rec/{cam}/{lens}, LiveDir = {LiveDir}/{cam}/{lens}, both NormalizePath'ed
}
```

`GET /api/cameras` → `200` JSON: `[{ "id", "name", "lenses": [{ "id", "ptz", "status": "starting|online|offline", "lastProgressAt", "restarts", "fps", "bitrate" }] }]` (camelCase, status lower-case).

- [ ] **Step 1: Write failing tests**

`BackoffTests`: `NextDelay(0)==1s`, `(3)==8s`, `(6)==60s`, `(10)==60s`.
`FakeFfmpegProcess : IFfmpegProcess` (test helper): `ReportProgress()` sets `LastProgressAt = clock.GetUtcNow()`; `Exit(int code)` completes `Exited`; `KillCalls` counter (`Kill()` also exits with -1).
`StreamWorkerTests` (FakeTimeProvider; worker started with `Task.Run`; after each `Advance` yield with `await Task.Delay(1)` loops until the expected state or 2 s wall-clock):
- `Progress_marks_stream_online`: start → fake reports progress → `Status == Online`, `Generation == 1`.
- `No_progress_for_10s_kills_and_restarts`: advance 11 s without progress → `KillCalls == 1`, a second fake was created, `Restarts == 1`.
- `No_video_for_60s_marks_offline`: keep advancing without progress through restarts → `Status == Offline`, `OfflineSince` set, `StatusChanged` raised once with previous `Starting`.
- `Progress_after_offline_marks_online_again` → `Status == Online`, event raised with previous `Offline`.
- `Restart_delay_follows_backoff`: second restart waits ≥ 2 s (no new factory call after advancing 1 s, one after 2 s).
- `Run_clears_live_dir_on_start`: pre-create `{LiveDir}/stale.ts` → after start the file is gone and the directory exists.
`StreamSupervisorTests`: `BuildSpecs_builds_one_spec_per_lens_with_dirs` (2 cams × 2 lenses → 4 specs; `RecDir == "/data/rec/cex-gate/ptz"`, `LiveDir == "/run/live/cex-gate/ptz"`; `RtspUrl` from `RtspUrlBuilder`).
`CamerasEndpointTests` (ApiFactory with fake ffmpeg and `cameras.dev.json`-like two cameras): `GET /api/cameras` → 200, two cameras, each lens has `status` field.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~Streams|FullyQualifiedName~Cameras"`
Expected: FAIL.

- [ ] **Step 3: Implement**

`StreamWorker.RunAsync` loop: `attempt = 0; firstVideoWaitStart = now`; loop while not cancelled: `Generation++`; `Directory.CreateDirectory(RecDir)`, recreate `LiveDir` empty; `proc = ffmpeg(FfmpegArgs.Recording(...), spec.Key.ToString())`; `await proc.StartAsync`; `startedAt = now`; monitor: `while (!proc.Exited.IsCompleted)` → `await clock.Delay(1 s, ct)`; copy `Fps/Bitrate/LastProgressAt` into state; `if (LastProgressAt is not null && Status != Online) { SetStatus(Online); attempt = 0; }`; `var lastVideo = proc.LastProgressAt ?? startedAt; if (now - lastVideo > StallTimeout) { proc.Kill(); break; }`; after exit: `await proc.DisposeAsync(); Restarts++`; `var lastSeen = state.LastProgressAt ?? firstVideoWaitStart; if (Status != Offline && now - lastSeen > OfflineAfter) SetStatus(Offline)`; `await clock.Delay(Backoff.NextDelay(attempt++), ct)`. Wrap the body in `try/catch (OperationCanceledException) when (ct.IsCancellationRequested) { proc?.Kill(); break; } catch (Exception ex) { log; delay backoff }`.

`StreamSupervisor.ExecuteAsync`: start workers for `BuildSpecs(cameras.CurrentValue, server.Value)`; subscribe `cameras.OnChange` with a 500 ms debounce (file watchers fire twice) → compute new specs; for running specs not in the new set (record equality) cancel + await + `store.Remove`; start the new ones. On stop: cancel all, await.

`CamerasEndpoints.MapCamerasEndpoints(this IEndpointRouteBuilder)`: joins `IOptionsMonitor<CamerasOptions>.CurrentValue.Items` with `StreamStateStore`. JSON options: `JsonSerializerDefaults.Web` plus `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CameraMonitor.Tests`
Expected: PASS.

- [ ] **Step 5: Manual check against the simulator**

Run: `docker compose -f deploy/compose.dev.yml up -d` then `dotnet run --project src/CameraMonitor.Api` → `curl localhost:8080/api/cameras` shows both lenses `online` within ~5 s; `data/rec/sim1/main/<date>/<hour>/` fills with `.ts` files every 10 s; `docker compose -f deploy/compose.dev.yml stop cam1` → `sim1/main` becomes `offline` within 60–75 s; `start cam1` → `online`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(streams): per-lens ffmpeg supervisor with stall detection, backoff and /api/cameras"
```

---

### Task 5: Live HLS endpoint, JPEG tiles, first pages

**Files:**
- Create: `src/CameraMonitor.Api/Infrastructure/SafeName.cs`, `src/CameraMonitor.Api/Infrastructure/ProcessRunner.cs`
- Create: `src/CameraMonitor.Api/Features/Streams/LiveEndpoints.cs`, `TileService.cs`
- Create: `src/CameraMonitor.Api/Features/Status/StatusEndpoints.cs` (streams only; Task 9 adds disk)
- Create: `src/CameraMonitor.Api/wwwroot/index.html`, `cam.html`, `status.html`, `app.css`, `app.js`, `vendor/hls.min.js`
- Modify: `Program.cs` (map endpoints, register `TileService` singleton)
- Test: `tests/CameraMonitor.Tests/Infrastructure/SafeNameTests.cs`, `tests/CameraMonitor.Tests/Streams/LiveEndpointTests.cs`, `TileServiceTests.cs`

**Interfaces:**
- Consumes: `StreamStateStore` (`RecDir`, `LiveDir`), `ServerOptions.FfmpegPath`.
- Produces:

```csharp
public static class SafeName { public static bool IsSafe(string segment); } // ^[A-Za-z0-9_.-]{1,64}$ and not "." / ".." and no ".." substring
public static class ProcessRunner
{
    public static Task<int> RunAsync(string exe, IReadOnlyList<string> args, Stream stdout, TimeSpan timeout, CancellationToken ct); // copies stdout to the stream, kills on timeout, returns exit code
    public static async Task<(int ExitCode, byte[] Stdout)> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);
}
public sealed class TileService(StreamStateStore store, IOptions<ServerOptions> server, TimeProvider clock, ILogger<TileService> logger)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(2);
    public Task<byte[]?> GetAsync(StreamKey key, CancellationToken ct); // null when no finished live segment; at most 4 concurrent ffmpeg grabs (SemaphoreSlim)
    public static string? PickLatestFinishedSegment(IReadOnlyList<string> tsFilesSortedByName); // second-newest file (the newest is still being written); null if < 2
}
```

Endpoints: `GET /live/{cam}/{lens}/{file}` → 400 if any segment fails `SafeName.IsSafe`, 404 if missing, else file with `Cache-Control: no-store`; `.m3u8` → `application/vnd.apple.mpegurl`, `.ts` → `video/mp2t`. `GET /api/tile/{cam}/{lens}.jpg` → `image/jpeg` + `Cache-Control: no-store`, 404 when `GetAsync` returns null. `GET /api/status` → `{ "streams": [ { "camera", "lens", "status", "lastProgressAt", "restarts", "generation", "fps", "bitrate" } ] }`.

Tile ffmpeg command: `-nostdin -loglevel error -i {segment} -frames:v 1 -vf scale=320:-2 -f image2 -c:v mjpeg -q:v 5 pipe:1`, timeout 5 s. `// ponytail: ffmpeg grab per tile; Plan B switches to DVRIP snapshot when measured faster`.

- [ ] **Step 1: Write failing tests**

`SafeNameTests`: `index.m3u8` ok; `..` no; `a..b` no; `a/b` no; `a\b` no; 65 chars no.
`LiveEndpointTests` (ApiFactory, write a dummy `index.m3u8` into the stream's live dir via `StreamStateStore.GetOrAdd` + state `LiveDir`): `Serves_playlist_with_no_store` (200, content type `application/vnd.apple.mpegurl`, header `no-store`); `Rejects_traversal` for `/live/a/b/..%2Findex.m3u8` and `/live/a/..%5Cb/x.ts` → 400 — Review Focus 3; `Missing_file_404`.
`TileServiceTests`: `PickLatestFinishedSegment_returns_second_newest` (`["1.ts","2.ts","3.ts"]` → `"2.ts"`; one file → null); `GetAsync_returns_null_when_live_dir_empty`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~SafeName|FullyQualifiedName~Live|FullyQualifiedName~Tile"`
Expected: FAIL.

- [ ] **Step 3: Implement endpoints, `TileService`, `ProcessRunner`**

Tile cache: `ConcurrentDictionary<StreamKey, (DateTimeOffset At, byte[] Jpeg)>`; return cached when younger than `CacheFor`.

- [ ] **Step 4: Pages**

Download `hls.min.js` (latest hls.js 1.x from jsDelivr; write the exact version in a comment on line 1 of `vendor/hls.min.js`'s sibling `vendor/VERSION`). `app.css`: dark theme (`#111` background, `#eee` text), grid `display:grid; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 8px`, 16px page gutter, status dot colours (online `#3fb950`, offline `#f85149`, starting `#d29922`). `app.js`: `api(path)` fetch helper, `fmtTime(iso)` → `toLocaleString('uk-UA')`.
`index.html` («Камери»): loads `/api/cameras` every 10 s, renders one tile per lens: `<img src="/api/tile/{cam}/{lens}.jpg?t=…">` refreshed every 2 s, caption `{name} · {lens}` + status dot + «онлайн/офлайн/запуск»; click → `cam.html?cam=…&lens=…`; nav links «Камери · Архів · Статус».
`cam.html`: `<video controls autoplay muted playsinline>`; hls.js on `/live/{cam}/{lens}/index.m3u8` with `{ liveSyncDurationCount: 3, lowLatencyMode: false }`; native HLS fallback via `canPlayType('application/vnd.apple.mpegurl')`; title from `/api/cameras`; «← Камери». (PTZ pad, timeline and the ≤ 2 s player come in Plan B.)
`status.html` («Статус»): table from `/api/status` refreshed every 5 s: камера, лінза, стан, останній кадр, рестарти, fps, бітрейт.

- [ ] **Step 5: Run all tests; manual check**

Run: `dotnet test tests/CameraMonitor.Tests` → PASS. `dotnet run` with the simulator → `http://localhost:8080/` shows two tiles updating with the moving clock, click opens live video within ~6 s, `/status.html` shows both online.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(web): live HLS endpoint, JPEG tile grid, camera and status pages"
```

---

### Task 6: SQLite index via EF Core

**Files:**
- Create: `src/CameraMonitor.Api/Infrastructure/Db/SegmentRow.cs`, `AppDbContext.cs`, `Migrations/` (generated)
- Modify: `CameraMonitor.Api.csproj` (`Microsoft.EntityFrameworkCore.Sqlite` 9.0.*, `Microsoft.EntityFrameworkCore.Design` 9.0.* with `PrivateAssets=all`), `Program.cs` (DbContext registration, migrate at startup), `ApiFactory.cs` (nothing: DataDir is already a temp dir)
- Test: `tests/CameraMonitor.Tests/Db/SegmentsDbTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed class SegmentRow
{
    public long Id { get; set; }
    public required string CameraId { get; set; }
    public required string LensId { get; set; }
    public DateTime StartUtc { get; set; }        // Kind=Utc
    public int DurationMs { get; set; }
    public required string Path { get; set; }     // relative to DataDir, forward slashes: rec/{cam}/{lens}/yyyyMMdd/HH/HHmmss.ts ; unique
    public long Bytes { get; set; }
    public bool Discontinuity { get; set; }
}
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<SegmentRow> Segments => Set<SegmentRow>();
    // indexes: (CameraId, LensId, StartUtc); unique (Path)
}
public static class DbSetup { public static Task MigrateAsync(IServiceProvider services, CancellationToken ct); } // creates {DataDir}/db, MigrateAsync, PRAGMA journal_mode=WAL
```

Connection string: `Data Source={DataDir}/db/cm.sqlite;Default Timeout=5`. `DateTime` stored as ISO text (EF default); read back with `Kind=Utc` via a value converter.

- [ ] **Step 1: Write the failing test**

`SegmentsDbTests` (ApiFactory → `Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>()`): `RoundTrip_and_range_query`: insert 3 rows for key `a/b` at 00:00:00, 00:00:10, 00:00:20 (10 000 ms each) and 1 row for `a/c`; query `a/b` with `StartUtc >= 00:00:05 && StartUtc < 00:00:25` → 2 rows ordered by `StartUtc`; `StartUtc.Kind == Utc`. `Duplicate_path_rejected` → `DbUpdateException`.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~Db"`
Expected: FAIL.

- [ ] **Step 3: Implement, generate the migration**

```bash
dotnet tool install -g dotnet-ef --version 9.*   # once
dotnet ef migrations add Initial --project src/CameraMonitor.Api --output-dir Infrastructure/Db/Migrations
```

Register `AddDbContext<AppDbContext>((sp, o) => o.UseSqlite(connString))`; call `await DbSetup.MigrateAsync(app.Services, ct)` before `app.RunAsync()`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CameraMonitor.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(db): SQLite segment index via EF Core with startup migration and WAL"
```

---

### Task 7: Segment indexing from `archive.m3u8`, orphan scanner

**Files:**
- Create: `src/CameraMonitor.Api/Features/Archive/HlsPlaylistParser.cs`, `SegmentIndexer.cs`, `DirectoryScanner.cs`, `IDurationProbe.cs`, `FfprobeDurationProbe.cs`
- Modify: `Program.cs` (register `IDurationProbe`, both hosted services)
- Test: `tests/CameraMonitor.Tests/Archive/HlsPlaylistParserTests.cs`, `SegmentIndexerTests.cs`, `DirectoryScannerTests.cs`

**Interfaces:**
- Consumes: `StreamStateStore` (`RecDir` per stream), `AppDbContext`, `ProcessRunner`.
- Produces:

```csharp
public sealed record PlaylistEntry(DateTimeOffset ProgramDateTime, double DurationSeconds, string FileName);
public static class HlsPlaylistParser { public static IReadOnlyList<PlaylistEntry> Parse(string m3u8); } // pairs #EXT-X-PROGRAM-DATE-TIME + #EXTINF + uri; accepts offsets "+0000", "+00:00", "Z"; entries without PDT are skipped
public interface IDurationProbe { Task<TimeSpan?> ProbeAsync(string absolutePath, CancellationToken ct); }
public sealed class FfprobeDurationProbe(IOptions<ServerOptions> server, ILogger<FfprobeDurationProbe> logger) : IDurationProbe // ffprobe -v error -show_entries format=duration -of csv=p=0 {path}, 5 s timeout
public sealed class SegmentIndexer(IServiceScopeFactory scopes, StreamStateStore store, IOptions<ServerOptions> server, TimeProvider clock, ILogger<SegmentIndexer> logger) : BackgroundService
{
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
    public static bool IsDiscontinuity(DateTime? previousEndUtc, DateTime startUtc); // null → true; |start - prevEnd| > 1500 ms → true
    public static string RelativePath(string dataDir, string absolutePath);            // forward slashes, relative to DataDir
    public Task IndexOnceAsync(CancellationToken ct);  // one poll over all streams (used by tests)
}
public sealed class DirectoryScanner(IServiceScopeFactory scopes, StreamStateStore store, IOptions<ServerOptions> server, IDurationProbe probe, TimeProvider clock, ILogger<DirectoryScanner> logger) : BackgroundService
{
    public static readonly TimeSpan MinAge = TimeSpan.FromSeconds(30);
    public static IReadOnlyList<string> RecentHourDirs(string recDir, int count); // hour directories ordered by LastWriteTimeUtc desc, top `count`
    public Task ScanAsync(string recDir, StreamKey key, bool full, CancellationToken ct); // full: every hour dir; else RecentHourDirs(recDir, 2)
}
```

Indexer rule per stream: read `{RecDir}/archive.m3u8` (ignore if missing); for each entry whose relative path is not yet in `Segments`: `StartUtc = entry.ProgramDateTime.UtcDateTime`, `DurationMs = round(DurationSeconds*1000)`, `Bytes = FileInfo.Length`, `Discontinuity = IsDiscontinuity(prevEnd[key], StartUtc)`; `prevEnd[key] = StartUtc + Duration`. `prevEnd` is seeded at startup from the DB (`max(StartUtc + DurationMs)` per key). Scanner rule: `.ts` files older than `MinAge` whose relative path is not in the DB → `duration = probe ?? SegmentSeconds`, `StartUtc = LastWriteTimeUtc - duration`, `Discontinuity = true`. Scanner runs `full: true` once at startup, then `full: false` every 60 s. The indexer also deletes stale files in each stream's live dir at startup (already done by the worker; nothing more here).

- [ ] **Step 1: Write failing tests**

`HlsPlaylistParserTests`: fixture

```
#EXTM3U
#EXT-X-VERSION:6
#EXT-X-TARGETDURATION:10
#EXT-X-MEDIA-SEQUENCE:12
#EXT-X-PROGRAM-DATE-TIME:2026-10-25T14:03:10.120+0000
#EXTINF:10.000000,
20261025/14/140310.ts
#EXT-X-PROGRAM-DATE-TIME:2026-10-25T14:03:20.120+00:00
#EXTINF:9.960000,
20261025/14/140320.ts
```

→ 2 entries; first `ProgramDateTime == 2026-10-25T14:03:10.120Z`, `DurationSeconds == 10.0`, `FileName == "20261025/14/140310.ts"`; second parsed with the colon offset. `Entry_without_pdt_is_skipped`. `Z_offset_parses`.
`SegmentIndexerTests`: `IsDiscontinuity(null, t) == true`; `(t, t) == false`; `(t, t+1.4s) == false`; `(t, t+2s) == true`; `(t, t-2s) == true`. `IndexOnceAsync_inserts_new_entries_once` (ApiFactory; create temp rec dir with the fixture playlist and two 1-byte `.ts` files; call twice → exactly 2 rows; second row `Discontinuity == false`; `Path == "rec/a/b/20261025/14/140320.ts"`).
`DirectoryScannerTests` (fake `IDurationProbe` returning 7 s): `Orphan_older_than_30s_is_indexed_with_probed_duration` (file not in playlist, LastWriteTime set 2 min ago → row with `DurationMs == 7000`, `StartUtc == LastWriteTimeUtc - 7 s`, `Discontinuity == true`) — Review Focus 5; `Fresh_file_is_skipped` (LastWriteTime now → no row); `RecentHourDirs_returns_two_newest`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~Archive"`
Expected: FAIL.

- [ ] **Step 3: Implement**

- [ ] **Step 4: Run tests to verify they pass; manual check**

Run: `dotnet test tests/CameraMonitor.Tests` → PASS. With the simulator running for 1 minute: `sqlite3 data/db/cm.sqlite "select CameraId, LensId, StartUtc, DurationMs, Discontinuity from Segments order by StartUtc desc limit 5"` shows ~10 000 ms rows; kill `dotnet run` with Ctrl+C mid-segment, restart → the partial file appears as a row with `Discontinuity = 1` within 90 s.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(archive): index completed segments from archive.m3u8, scan for orphans with ffprobe"
```

---

### Task 8: Archive playback, timeline, export, archive page

**Files:**
- Create: `src/CameraMonitor.Api/Features/Archive/PlaylistBuilder.cs`, `TimelineBuilder.cs`, `ArchiveEndpoints.cs`, `ExportService.cs`
- Create: `src/CameraMonitor.Api/wwwroot/archive.html`
- Modify: `Program.cs` (map endpoints, register `ExportService`), `wwwroot/app.js` (nav), `cam.html` («Архів» link with cam/lens)
- Test: `tests/CameraMonitor.Tests/Archive/PlaylistBuilderTests.cs`, `TimelineBuilderTests.cs`, `ArchiveEndpointTests.cs`

**Interfaces:**
- Consumes: `SegmentRow`, `AppDbContext`, `SafeName`, `ProcessRunner`, `ServerOptions`.
- Produces:

```csharp
public static class PlaylistBuilder { public static string BuildVod(IReadOnlyList<SegmentRow> segments, Func<SegmentRow, string> segmentUrl); }
public sealed record TimeRange(DateTime FromUtc, DateTime ToUtc);
public static class TimelineBuilder { public static IReadOnlyList<TimeRange> Merge(IEnumerable<SegmentRow> orderedByStart, TimeSpan maxGap); }
public sealed class ExportService(IOptions<ServerOptions> server, ILogger<ExportService> logger)
{
    public static readonly TimeSpan MaxRange = TimeSpan.FromHours(2);
    public Task<int> WriteMp4Async(IReadOnlyList<string> absoluteSegmentPaths, Stream output, CancellationToken ct); // writes a concat list file to a temp dir, runs ffmpeg, deletes it
}
```

`BuildVod` output for two 10 000 ms segments where the second has `Discontinuity = true` (exact text, `\n` line endings, `EXTINF` with 3 decimals):

```
#EXTM3U
#EXT-X-VERSION:3
#EXT-X-TARGETDURATION:10
#EXT-X-MEDIA-SEQUENCE:0
#EXT-X-PLAYLIST-TYPE:VOD
#EXTINF:10.000,
/seg/a/b/20261025/14/140310.ts
#EXT-X-DISCONTINUITY
#EXTINF:10.000,
/seg/a/b/20261025/14/140330.ts
#EXT-X-ENDLIST
```

`TARGETDURATION` = ceil(max duration in seconds). Endpoints:
- `GET /archive/{cam}/{lens}.m3u8?from=&to=` — `from`/`to` ISO-8601 (`DateTimeOffset`), both required; 400 (`ProblemDetails`, Ukrainian `detail`) when `from >= to` or `to - from > 24 h` or ids unsafe; selects rows with `StartUtc < to && StartUtc >= from - 2*SegmentSeconds` then keeps those with `StartUtc + Duration > from`; segment URL `/seg/{Path without "rec/" prefix}`; content type `application/vnd.apple.mpegurl`, `Cache-Control: no-store`.
- `GET /seg/{cam}/{lens}/{date}/{hour}/{file}` — `date` `^\d{8}$`, `hour` `^\d{2}$`, `file` `^\d{6}\.ts$`, ids `SafeName`; else 400; `video/mp2t`, `Cache-Control: private, max-age=86400`.
- `GET /api/timeline?cam=&lens=&day=yyyy-MM-dd` — UTC day; `{ "ranges": [{ "from", "to" }], "events": [] }` (`Merge` with `maxGap` 1.5 s; `events` filled in Plan B).
- `GET /api/export?cam=&lens=&from=&to=` — 400 when range > `MaxRange` or invalid; else `video/mp4`, `Content-Disposition: attachment; filename="{cam}-{lens}-{from:yyyyMMdd-HHmm}.mp4"`, body streamed from `WriteMp4Async`. ffmpeg: `-nostdin -loglevel error -f concat -safe 0 -i {list} -c copy -f mp4 -movflags frag_keyframe+empty_moov pipe:1`. `// ponytail: concat across discontinuities may stutter at gap boundaries; re-mux per range if it matters`.

- [ ] **Step 1: Write failing tests**

`PlaylistBuilderTests`: `BuildVod_matches_expected_text` (exact string above); `BuildVod_empty_has_header_and_endlist`; `TargetDuration_rounds_up` (9 960 ms → 10).
`TimelineBuilderTests`: three contiguous 10 s segments → one range 00:00–00:30; a 5 s gap splits into two ranges; a 1 s gap does not.
`ArchiveEndpointTests` (ApiFactory, seed rows through `AppDbContext`): `Playlist_returns_overlapping_segments` (seed 3, request a window covering 2 → body has exactly 2 `.ts` lines); `From_after_to_is_400`; `Range_over_24h_is_400`; `Export_over_2h_is_400` — Review Focus 4; `Seg_rejects_traversal` (`/seg/a/b/20261025/14/..%2F..%2Fx.ts` → 400 or 404, never 200) — Review Focus 3; `Timeline_merges_ranges` (seed 3 contiguous → 1 range).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~Archive"`
Expected: FAIL.

- [ ] **Step 3: Implement endpoints and services**

- [ ] **Step 4: `archive.html` («Архів»)**

Controls: camera/lens `<select>` (from `/api/cameras`), `<input type="date">` (default today), timeline bar: a 24-hour `<div>` with absolutely positioned range blocks from `/api/timeline` (hover shows time), click → `from` at that time, loads `/archive/{cam}/{lens}.m3u8?from=&to=from+1h` into hls.js, red cursor follows `video.currentTime`; `<select>` speed (1×, 2×, 4×, 8× → `playbackRate`); export: two `<input type="datetime-local">` prefilled with from / from+10 min and a «Експорт MP4» link to `/api/export`. Times shown in the browser's local zone (`uk-UA`). All UI text Ukrainian.

- [ ] **Step 5: Run all tests; manual check**

Run: `dotnet test tests/CameraMonitor.Tests` → PASS. With ≥ 10 minutes of simulator recording: open `/archive.html`, pick sim1, click into the recorded block → playback starts within 3 s and seeks; export 2 minutes → the MP4 opens in VLC/Chrome.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(archive): VOD playlists from the index, timeline, MP4 export and archive page"
```

---

### Task 9: Retention and disk status

**Files:**
- Create: `src/CameraMonitor.Api/Features/Archive/RetentionPlanner.cs`, `RetentionService.cs`, `RetentionStatus.cs`
- Modify: `Features/Status/StatusEndpoints.cs` (`disk` block), `wwwroot/status.html` (disk panel), `Program.cs` (register)
- Test: `tests/CameraMonitor.Tests/Archive/RetentionPlannerTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record RetentionInput(IReadOnlyList<SegmentRow> OldestFirst, DateTime NowUtc, TimeSpan Retention, long FreeBytes, long MinFreeBytes);
public static class RetentionPlanner
{
    public static readonly TimeSpan NeverDeleteYoungerThan = TimeSpan.FromHours(1);
    public static IReadOnlyList<SegmentRow> Plan(RetentionInput input); // walks oldest-first: delete while (age > Retention) || (FreeBytes + reclaimed < MinFreeBytes); stops at the first segment younger than 1 h
}
public sealed class RetentionStatus { public DateTime? OldestSegmentUtc { get; set; } public long FreeBytes { get; set; } public long ArchiveBytes { get; set; } public DateTime? LastRunUtc { get; set; } public int LastDeleted { get; set; } } // singleton
public sealed class RetentionService(IServiceScopeFactory scopes, IOptions<ServerOptions> server, RetentionStatus status, TimeProvider clock, ILogger<RetentionService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    public Task RunOnceAsync(CancellationToken ct); // loads oldest 5000 rows, DriveInfo(DataDir).AvailableFreeSpace, Plan → delete files (ignore missing) + rows, remove empty hour/day dirs, update status
}
```

`/api/status` adds `"disk": { "freeGb", "archiveGb", "oldestSegmentUtc", "effectiveRetentionDays", "minRetentionDays", "lastRunUtc", "lastDeleted" }` (`effectiveRetentionDays = (now - oldest).TotalDays` rounded to 0.1). `status.html` shows it under «Диск та архів», highlighting `effectiveRetentionDays < minRetentionDays` in red.

- [ ] **Step 1: Write failing tests**

`RetentionPlannerTests`: `Deletes_segments_older_than_retention` (3 rows aged 20 d, 15 d, 1 d, retention 14 d, plenty of free space → first two); `Deletes_oldest_until_free_space_met` (five 1 GB rows aged 2–6 d, free 1 GB, min 3 GB → oldest two); `Keeps_segments_younger_than_1h_even_when_disk_full` (rows aged 30 min, free 0 → none); `Nothing_to_do_returns_empty`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CameraMonitor.Tests --filter "FullyQualifiedName~Retention"`
Expected: FAIL.

- [ ] **Step 3: Implement**

- [ ] **Step 4: Run tests; manual check**

Run: `dotnet test tests/CameraMonitor.Tests` → PASS. Set `Server:RetentionDays=0` and `RetentionPlanner.NeverDeleteYoungerThan` left as is: after 5 minutes nothing younger than 1 h is deleted; with a 70-minute recording, the first 10 minutes disappear and `/api/status` `lastDeleted > 0`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(archive): retention by age and free space, disk status"
```

---

### Task 10: Docker image, production compose, setup notes, end-to-end check

**Files:**
- Create: `deploy/Dockerfile`, `deploy/docker-compose.yml`, `deploy/secrets.example/cam_password`, `deploy/.dockerignore`, `docs/SETUP.md`
- Modify: `README.md` (production run section)

**Interfaces:** none new.

- [ ] **Step 1: Dockerfile**

Multi-stage: `mcr.microsoft.com/dotnet/sdk:9.0` → copy `*.sln`, `src/**/*.csproj`, `tests/**/*.csproj` → `dotnet restore` → copy all → `dotnet publish src/CameraMonitor.Api -c Release -o /app`; runtime `mcr.microsoft.com/dotnet/aspnet:9.0` → `apt-get install -y --no-install-recommends ffmpeg tzdata curl`, `ENV TZ=Europe/Kyiv ASPNETCORE_HTTP_PORTS=8080`, `USER app`, `HEALTHCHECK CMD curl -fsS http://localhost:8080/health || exit 1`, `ENTRYPOINT ["dotnet", "CameraMonitor.Api.dll"]`. `.dockerignore`: `**/bin`, `**/obj`, `data`, `secrets`, `.git`.

- [ ] **Step 2: `deploy/docker-compose.yml`**

Service `cameramonitor`: `build: { context: .., dockerfile: deploy/Dockerfile }`, `network_mode: host`, `restart: unless-stopped`, volumes `./data:/data`, `./cameras.json:/app/cameras.json:ro`, `./secrets:/run/secrets:ro`, `tmpfs: ["/run/live:size=512m"]`, environment `Server__CamerasFile=/app/cameras.json`, `Serilog__MinimumLevel__Default=Information`, logging `json-file` `max-size: 10m`, `max-file: 5`, healthcheck `curl -fsS http://localhost:8080/health`.

- [ ] **Step 3: `docs/SETUP.md` (Ukrainian)**

Sections: server (Ubuntu 24.04, Docker Engine, HDD mounted at `deploy/data`, `sudo chown -R 1654:1654 deploy/data` for the non-root `app` user), network (camera VLAN without internet route, static IPs), per-camera one-time steps in iCSee before cutting the cloud (set admin password, enable RTSP/ONVIF under advanced settings), `cameras.json` from the example, `secrets/cam_password`, `docker compose up -d --build`, where the UI is (`http://<server>:8080/`), Tailscale for remote access (link to install docs; no config needed). Plan B/C items marked «буде».

- [ ] **Step 4: Build and run end to end**

Run: `docker compose -f deploy/docker-compose.yml build` → image builds. Copy `deploy/cameras.dev.json` to `deploy/cameras.json`, `docker compose -f deploy/compose.dev.yml up -d`, `docker compose -f deploy/docker-compose.yml up -d` → `http://localhost:8080/` shows both simulator tiles; archive files under `deploy/data/rec`; `docker compose -f deploy/compose.dev.yml stop cam1` → sim1 offline in `/status.html` within 75 s; `start cam1` → online; `docker compose -f deploy/docker-compose.yml restart cameramonitor` → streams return and the partial segment gets indexed. (Linux host needed for `network_mode: host`; on this Windows machine verify the image builds and run the app natively for the rest.)

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore(deploy): Dockerfile, production compose, SETUP notes"
```

---

## Self-review notes

- Spec coverage for M0–M2: skeleton/sim (T1), config + RTSP (T2), ffmpeg + tee + GOP-independent liveness (T3–T4), live HLS + JPEG grid (T5), SQLite (T6), indexing without FileSystemWatcher + orphans (T7), VOD playlists with discontinuities, timeline, export ≤ 2 h (T8), retention by days/free space + `retention_short` data for Plan C (T9), Docker/compose/SETUP (T10). Not in this plan by design: GOP/smart-encode configuration on the camera (DVRIP, Plan B — on the simulator `-g 15` already gives 1 s GOP), `camera_offline` alerts (Plan C consumes `StreamStateStore.StatusChanged`), auth (Plan C).
- Types used across tasks: `StreamKey`, `StreamState.RecDir/LiveDir`, `FfmpegFactory`, `SegmentRow.Path` (relative, `rec/...`), `SafeName`, `ProcessRunner` — defined once, referenced by name afterwards.
