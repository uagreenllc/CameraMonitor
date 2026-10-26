# Plan A — відкладені дрібниці та рішення (з журналу виконання, 2026-10-25/26)

Фінальний рев'юер (вся гілка) позначив: усе з цього списку може чекати планів B/C, крім уже виправленого хвилею (сканер, логи, ретеншен, CI, валідація шляхів, гаряче перезавантаження, backoff, ізоляція тестів). Запарковано: cam.html без відновлення hls.js після помилки → перший коміт плану B.

Task 1: minor (deferred): .gitignore `data/`/`secrets/` unanchored (match at any depth) → anchor as `/data/`, `/secrets/`, `/deploy/data/`
Task 1: minor (deferred): ci.yml runs on every push and PR (double runs) → restrict push to master
Task 1: minor (deferred): linuxserver/ffmpeg:latest unpinned in compose.dev.yml (dev-only)
Task 1: minor (deferred): ApiFactory.Dispose Directory.Delete may throw once ffmpeg holds live files → best-effort try/catch
Task 1: minor (deferred): Properties/launchSettings.json is template output (launchBrowser true), harmless
Task 2: minor (deferred): CameraPasswords File.ReadAllText may throw during ValidateOnStart → catch IOException/UnauthorizedAccessException, return null
Task 2: minor (deferred): RtspUrlBuilder treats empty-string lens.RtspUrl as valid → use IsNullOrWhiteSpace
Task 2: minor (deferred): forbidden-password test covers only `&` → Theory over the full set
Task 2: minor (deferred): validator does not check Host format/characters (e.g. `host/path`, `user@host` would make a malformed URL)
Task 3: minor (deferred): FfmpegProcess.LastProgressAt (DateTimeOffset?) written/read cross-thread may tear → store UtcTicks in a long with Volatile/Interlocked
Task 3: minor (deferred): StartAsync called twice leaks the first process (no guard)
Task 3: minor (deferred): DisposeAsync could throw a non-InvalidOperationException from Kill in a rare path
Task 3: minor (deferred): ProgressParserTests lack out_time_ms, progress=end, `=`-in-value cases
Task 3: minor (deferred): kill test does not assert exit code != 0
Task 4: minor (deferred): StatusChanged handler exception propagates into the worker loop and kills ffmpeg → guard the invoke (load-bearing for Plan C AlertEngine; Plan C must also catch in its handler)
Task 4: minor (deferred): torn reads of StreamState DateTimeOffset?/double across threads; FfmpegProcess.LastProgressAt not Volatile (same as T3 minor)
Task 4: minor (deferred): if startup ApplyAsync throws, the supervisor exits before subscribing OnChange → never recovers on a config fix
Task 4: minor (deferred): linked CancellationTokenSources in the debounce never disposed
Task 4: minor (deferred): Restarts not incremented on the exception path (StartAsync/factory throws)
Task 4: minor (deferred): worker tests advance fixed tick counts with 15 ms wall-clock per fake second → may flake under load; prefer advance-until-condition
Task 4: minor (deferred): test cleanup leaves empty cm-tests-data/ and cm-tests/ parents
Task 4: minor (deferred): a config change between the first ApplyAsync and the OnChange subscription is missed
Task 4: minor (deferred): Offline is evaluated only when a generation ends, so with growing backoff it lands at ~70–80 s of silence rather than 61 s; evaluating per monitor tick would tighten it
Task 5: minor (deferred): LiveEndpoints File.Exists→Results.File races with ffmpeg segment deletion (500 on rotation); add explicit "full path StartsWith LiveDir" check for defence in depth
Task 5: minor (deferred): ProcessRunner leaves the process running on non-cancellation exceptions mid-copy; Win32Exception for missing ffmpeg surfaces as 500 from the tile endpoint
Task 5: minor (deferred): no tests for tile endpoint 400/404/headers, ProcessRunner timeout kill, TileService cache/gate, /api/status
Task 5: minor (deferred): index.html refreshImages fires every 2 s even while the previous request is in flight
Task 5: minor (deferred): cam.html has no hls.js fatal-error recovery (startLoad/recoverMediaError) and no "unsupported" message
Task 5: minor (deferred): statusLabel inserted without esc() (server-controlled values)
Task 5: minor (deferred): TileService _cache never evicts entries (bounded by key count); cancelled waiters do not stop an in-flight grab (bounded by 5 s)
Task 6: minor (deferred): ApiFactory.DeleteBestEffort catches only IOException and gives up silently after 5 tries; SqliteConnection.ClearAllPools() after host dispose would be deterministic
Task 6: minor (deferred): StartUtc converter is identity on write — callers must pass UTC (Task 7 instructed); ToUniversalTime() on write would harden
Task 7: minor (deferred): EF logs every command at Information in Development (noisy at 2 s poll) → set Microsoft.EntityFrameworkCore.Database.Command to Warning in appsettings.Development.json
Task 7: minor (deferred): after a DbUpdateException the indexer does not advance prevEnd → possible false Discontinuity on the next entry; all DB failures logged at Debug as "already indexed"
Task 7: minor (deferred): no per-stream try/catch in either loop → one stream's DirectoryNotFoundException skips the rest of the pass
Task 7: minor (deferred): prevEnd seed uses latest StartUtc row rather than max(StartUtc+DurationMs)
Task 7: minor (deferred): IndexOnceAsync test shares the singleton with the hosted 2 s poller → slow-machine flake risk; remove the hosted indexer in that test
Task 7: minor (deferred): missing tests: real ffmpeg shape (EXTINF before PDT, cwd-relative URI), prevEnd seeding after restart, DbUpdateException path, scanner skipping known file, scanner Kind==Utc
Task 7: minor (deferred): indexer re-reads/queries every 2 s even when the playlist is unchanged (skip on same mtime+length)
Task 7: minor (deferred): `real-ffmpeg` xUnit collection fix is unproven (root cause of the flake not isolated)
Task 7: minor (deferred): a ScanAsync exception for one stream propagates out of RunPassAsync and skips the rest of the pass; startup wait has no test
Task 8: minor (deferred): no endpoint tests for export 200/404 or /seg 404/bad-regex paths
Task 8: minor (deferred): archive.html — DST day math drifts 1 h; click before first load plays from 1970; cursor assumes contiguous playlist; playbackRate not applied on the native-HLS path
Task 8: minor (deferred): -2 timeout sentinel undocumented and logged as "ffmpeg exit code -2"; timeout unit test depends on ffmpeg on PATH; 200 assertion sits inside the throw-expected lambda
Task 9: minor (deferred): RetentionService tolerates only FileNotFound/DirectoryNotFound on File.Delete → a locked/denied oldest file aborts the whole run every time; catch per file and skip the row
Task 9: minor (deferred): RemoveIfEmpty catches only IOException (UnauthorizedAccessException skips the status update)
Task 9: minor (deferred): RetentionServiceTests seeds while the hosted service's first pass may run → theoretical flake
Task 10: minor (deferred): deploy/cameras.json not git-ignored (site-specific file)
Task 10: minor (deferred): SETUP.md field list omits the top-level `Cameras` wrapper; no warning that compose creates a directory if cameras.json is missing
Parked — B3 cam.html has no hls.js error recovery (live page needs a manual reload after a stream restart) — Ruling: real but not load-bearing; Plan B (M4) replaces this player with the WebSocket/MSE one, so the 5-line handler goes in as the first commit of Plan B (or now on the user's say-so) — cost if wrong: an office wall screen needs a reload after a camera hiccup until Plan B lands.
Minor (deferred): .dockerignore does not exclude deploy/config (site config enters the build context; not copied into the published image).
Minor (deferred): a persistently undeletable file stays at the head of the retention batch; ≥5000 locked rows would stall retention (not realistic).

## Рішення контролера
Ruling: feature branch in the main checkout instead of a git worktree — the repo is brand new, nothing else works in it, and the session cannot move its cwd (it lives in an Oberig worktree), so a second folder would only add friction — cost if wrong: none (no concurrent work in this checkout).
Ruling: StreamWorker sets them from StreamSpec when it starts (GetOrAdd then assign) — cost if wrong: endpoints 404 until fixed 
Ruling: indexer does nothing with live dirs; the worker owns them — cost if wrong: none 
Ruling: `Task.Delay(TimeSpan, TimeProvider, ct)` instead of the brief's non-existent `clock.Delay` — cost if wrong: none. Deviation noted: worker tests written after the implementation (no RED run). Tests use relative dirs because NormalizePath rejects Windows drive colons.
Ruling: reset attempt whenever the current process reported progress, independent of the status transition — spec wants backoff to recover on success (Oberig NextDelay pattern) — cost if wrong: a flapping camera reconnects faster than intended. Fix round 1 → resume implementer. FIX_BASE df3a0ce.
Ruling: live .ts files are ordered by LastWriteTimeUtc, not by name (HLS muxer names index9.ts/index10.ts, name order breaks) — cost if wrong: tile shows a slightly stale frame.
Ruling: add IDesignTimeDbContextFactory<AppDbContext> so `dotnet ef migrations add` does not have to boot Program.cs (which requires cameras.json) — cost if wrong: none.
Ruling: keep Debian's ffmpeg 5.1 in the image — every flag we use (hls strftime_mkdir, program_date_time, independent_segments, tee onfail, -progress) exists since ffmpeg 4.x; playlist parser is order/URI-shape agnostic — cost if wrong: verify on the real server; fallback: aspnet:9.0-noble (ffmpeg 6.1) or a static build. Reviewer dispatched (sonnet).
Ruling: fix wave = all 9 Important + minors tied to correctness (zero-byte orphans, RecentHourDirs by name, .gitignore) in ONE dispatch (opus); remaining minors stay deferred to Plans B/C — cost if wrong: a larger fix diff for the single re-review.
Ruling: Docker mounts a config directory (./config → /app/config) + DOTNET_USE_POLLING_FILE_WATCHER=1 so cameras.json hot reload works; SETUP/README updated — cost if wrong: none.
Ruling: real but not load-bearing; Plan B (M4) replaces this player with the WebSocket/MSE one, so the 5-line handler goes in as the first commit of Plan B (or now on the user's say-so) — cost if wrong: an office wall screen needs a reload after a camera hiccup until Plan B lands.
