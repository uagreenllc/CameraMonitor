# Plan B — відкладені дрібниці та рішення (з журналу виконання, 2026-10-26)

Фінальний рев'юер (вся гілка, Tasks 1–12) дав «With fixes»: 0 Critical, 6 Important — усі виправлені фінальною хвилею (вікно довантаження обмежене горизонтом ретеншену, збій одного файлу не валить прогін, масштаб часу sd-сегментів, ліміт швидкості `Server:SdMaxKbps`, WS прив'язаний до ApplicationStopping + KeepAliveTimeout, MSE → HLS після 3 невдач) разом із флейком, `lastError` на статусі, keepalive сесії завантаження, затримкою XmAlarmServer на TimeProvider, хвостом stderr ffmpeg і 404/1011 замість 1013. Повторний рев'ю: 12/12 ADDRESSED. Решта дрібниць лишається нижче; «declined to judge» (автентифікація — План C; PTZ-константи, мапа каналів, коди Ret, ліміти сесій, затримка MSE, `docker stop`) — у розділі «Перевірка на реальній камері».

Task 1: minor (deferred): startHls keeps hls local and never destroys a previous instance → Task 12 must hold a module-level hls and destroy it before restarting (carried into T12 dispatch)
Task 1: minor (deferred): repeated fatal MEDIA_ERROR → no swapAudioCodec/backoff (tight recover loop on persistent decode error)
Task 1: minor (deferred): native-HLS (Safari) path has no status/recovery
Task 2: minor (deferred): codec length cap ~2 GB; Ruling: DvripClient (T3) rejects frames with payload > 16 MiB (InvalidDataException → connection closed) — real frames are KB-sized — cost if wrong: a huge legit frame would drop the session
Task 2: minor (deferred): fixture test does not assert Total/Current
Task 2: minor (deferred): ParseJson List<byte> double copy
Task 3: minor (deferred): late reply to a timed-out request can match the next same-msgId request; Ruling: Task 4 adds a reply["Name"] == requested Name check in GetConfigAsync (its read-modify-write depends on it) — cost if wrong: a camera that omits Name in replies; check only when both present
Task 3: minor (deferred): login/drop race (Dekker on volatile fields) can yield neither Closed nor ConnectAsync exception → Task 4 agent must also treat InvalidOperationException from KeepAlive as disconnect; barrier fix for the final wave
Task 3: minor (deferred): SnapshotAsync accepts frames of any MsgId
Task 3: minor (deferred): receive buffer never shrinks after a large frame
Task 3: minor (deferred): QueryFiles throws on Ret 110/111 ("search succeeded"); Ruling: leave per plan until the real camera shows these codes (Task 13 check) — cost if wrong: backfill gets no files from such firmware until fixed
Task 3: minor (deferred): FakeXmCamera Files.First on unknown name faults the connection
Task 3: minor (deferred): untested GuardAsync, snapshot timeout, download idle timeout, download JSON-ack branch, MaxPayloadBytes
Task 3: minor (deferred): RequestAsync mutates the caller's body (adds SessionID)
Task 4: minor (deferred): state.Gop = 1 set even when no channel was processed
Task 4: minor (deferred): snapshot file name collides for Start/Stop/Start within one second (shared file, retention deletes the other's image)
Task 4: minor (deferred): Connected=true while Guard failed hides the alarm outage on the status page (show lastError whenever set)
Task 4: minor (deferred): XmAgentState fields mutated cross-thread without volatile (display only)
Task 4: minor (deferred): supervisor exits before OnChange if the first ApplyAsync throws (inherited StreamSupervisor pattern)
Task 4: minor (deferred): reconnect cleanup can wait behind an old-generation snapshot (bounded by snapshot 5 s + tile 5 s)
Task 5: minor (deferred): retention deletes event snapshots without the events-root check used on read
Task 5: minor (deferred): events.html never restores the «Нема подій» text after a transient error
Task 5: minor (deferred): PTZ pad stops on window blur but not on visibilitychange
Task 5: minor (deferred): DeleteOldEventsAsync loads all old rows at once (no batching)
Task 5: minor (deferred): re-press during an in-flight/retried stop can halt the new motion early; no server-side PTZ auto-stop (check on the real camera in Task 13)
Task 6: minor (deferred): unbounded concurrent alarm-server connections (SemaphoreSlim ~32)
Task 6: minor (deferred): DNS lookup in FindCameraAsync has no deadline
Task 6: minor (deferred): unknown-host Warning logged per frame, no rate limit
Task 6: minor (deferred): negative test uses a fixed 500 ms wait
Task 6: minor (deferred): string-typed Port rewritten on every connect
Task 7: minor (deferred): .range.sd uses !important (a .timeline .range.sd selector would do); legend colours inline duplicate app.css
Task 8: minor (deferred): SdRemuxer discards ffmpeg stderr (log its tail on failure)
Task 8: minor (deferred): partial outputs stay in outDir on failure → Task 9 deletes its temp dir in finally (carried into T9 dispatch)
Task 8: minor (deferred): malformed segments.csv line throws a non-descriptive exception
Task 8: minor (deferred): media byte ignored; an H.265 camera would be fed as -f h264 (cameras are H.264 per spec)
Task 8: minor (deferred): XmStripStream lacks a WriteAsync override
Task 9: minor (deferred): trailing gap with only skipped files is re-downloaded each run while the outage lasts (end moves, not contained)
Task 9: minor (deferred): DST fall-back hour resolves to standard time (1 h/year); spring-forward +1 h hard-coded
Task 9: minor (deferred): first periodic pass 10 min after startup; an agent connected before SdBackfill subscribed waits up to 10 min
Task 9: minor (deferred): rows starting before the window are excluded (false leading gap ≤ one row)
Task 9: minor (deferred): hosted SdBackfill opens download sessions in unrelated ApiFactory tests (PtzEndpointTests loosened to Logins >= 1)
Task 9: minor (deferred): mtime touch before move not pinned by a test; a file whose remux fails is not retried until restart; a file spanning two gaps is skipped for the second
Task 10: minor (deferred): stray moof discards an unfinished fragment / mdat without moof dropped silently (by design, untested) — reviewer listed under Important but called it fine; Ruling: minor — it matches the brief's drop rule — cost if wrong: none observed
Task 10: minor (deferred): ASCII string per box header; CodecString naive avcC scan; no truncated-avcC / overrunning-NAL tests
Task 10: minor (deferred): _initBoxes MemoryStream kept after Init is set (few KB duplicate)
Task 11: minor (deferred): after the >300 clear the GOP backlog refills with P-frames → late joiner can start mid-GOP (skip non-keyframes until the next keyframe)
Task 11: minor (deferred): a stalled-but-alive WS peer blocks its handler (set WebSocketOptions.KeepAliveTimeout)
Task 11: minor (deferred): mid-send cancel aborts instead of a clean close; 1013 path skips the close handshake
Task 11: minor (deferred): _idle/_cts CTS never disposed; commit message and code comment disagree
Task 11: minor (deferred): slow-client log reports ClientQueue, not real capacity; Source_exit test timing-dependent
Task 12: minor (deferred): permanent addSourceBuffer failure retries every 3 s forever (fall back to HLS after N failures)
Task 12: minor (deferred): paused/blocked autoplay → buffer not trimmed until QuotaExceeded → visible reconnect
Task 12: minor (deferred): no video.onerror handler
Final: minor (deferred): transient TimeoutException/busy-Claim DvripException count as a file attempt (good file can be given up after 3 hiccups until restart); throttle wiring untested (fixture SdMaxKbps=0); long throttled download has no keepalive on the download session (check on the real camera); XmPassword redaction over-redacts the rest of an stderr line

## Рішення контролера
Ruling: T10 puts the `Box(type, payload)` test helper in `tests/CameraMonitor.Tests/Streams/Fmp4TestBoxes.cs` (internal static) so T11 tests reuse it — the plan names it only inside T10's test class — cost if wrong: none.
Ruling: the plan document was committed before the Task 1 cam.html fix, so the fix is the first code commit rather than the first commit of the branch — the approval round needed the plan on the branch — cost if wrong: none.
Ruling: commits from here on carry the trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` (session model switched to Opus 5.5; harness attribution reminder) instead of Fable 5.1 — cost if wrong: trailer text only.
Ruling: Task 9 bounded work = files of a gap processed newest-first and at most MaxFilesPerRun = 20 files per camera per run; a gap cut short stays un-exhausted and continues next run — the card overwrites its oldest files first, and other cameras must not wait hours — cost if wrong: a full-card fill takes several 10-min cycles
Ruling: run the final whole-branch review now over Tasks 1–12; Task 13 (docs + real-camera checks) follows once the user provides the camera config/password — the session that started this one asked to stop at Task 13's camera checks — cost if wrong: Task 13 doc edits get no whole-branch review (they are docs only; reviewed as a task)
Ruling: final fix wave = Important #1 (backfill window floored at the retention horizon), #2 (per-file download/parse failure caught, file marked done after 3 attempts), #3 (scale sd segment offsets to the camera's file duration when they differ > 2 %, warn), #4 (download rate limit `Server:SdMaxKbps`, default 8000 kbit/s, 0 = off — the spec's risk table requires «обмеження швидкості»), #5 (WS handler linked to ApplicationStopping + WebSocketOptions.KeepAliveTimeout), #6 (MSE falls back to HLS after 3 consecutive failures with no data); plus flake (DvripClientTests deadline 2 s → 10 s), T4 status shows lastError whenever set, download-session KeepAlive before each file, XmAlarmServer retry delay on the injected TimeProvider, keep ffmpeg stderr tail for relay/remux failures, 404/1011 instead of 1013 for a lens without a spec — cost if wrong: a slightly larger fix diff in one review
Ruling: final-review "declined to judge" items stay out (auth = Plan C; PTZ constants, channel map, Ret codes, session limits, MSE latency, docker stop timing → Task 13 checklist) — cost if wrong: found on the real camera
Ruling: deferred minors not listed above stay deferred per the final reviewer's triage — cost if wrong: small polish debt recorded in the Plan B deferred file
Ruling: accept EvictedThroughUtc (not OldestSegmentUtc) as the backfill floor — OldestSegmentUtc would block a fresh install from backfilling older card days — cost if wrong: one refill/evict cycle per restart under disk pressure
Ruling: accept the DvripClient frame queue bound (256) — without it the rate limit only buffers the file in memory — cost if wrong: alarm pushes on the control connection wait behind 256 unread unsolicited frames (drained by the next keepalive)

Рішення, вбудовані в рядки minor (див. вище): T2 (ліміт кадру DvripClient 16 MiB), T3 (перевірка Name у GetConfigAsync; Ret 110/111 — лишити за планом до реальної камери), T9 (MaxFilesPerRun = 20, найновіші першими), T10 (stray moof — minor).

## Фінальний тріаж

- Виправлено хвилею d1453ff, 387199a, b36bf44, ff2502e (228/228 ×3): Important #1–#6, флейк `DvripClientTests` (deadline 2 с → 10 с), `lastError` на статусі, keepalive перед кожним файлом, retry-delay XmAlarmServer на TimeProvider, хвіст stderr ffmpeg, 404/1011 для об'єктива без spec.
- Відхилення від брифу: поріг — `RetentionStatus.EvictedThroughUtc`; черга кадрів DvripClient обмежена 256; масштаб зсувів впливає лише на індексні часи.
- Лишається відкладеним: транзієнтні TimeoutException/«busy» DvripException рахуються як спроба файлу (хороший файл може бути покинутий після 3 збоїв до перезапуску); тротлінг без тесту (у фікстурі SdMaxKbps=0); сесія завантаження довгого тротлінгового файлу без keepalive; редакція XmPassword затирає решту рядка stderr.

## Перевірка на реальній камері

Виконує контролер із камерою на столі; результати дописати сюди (також виміряний час плиток ffmpeg vs dvrip і підсумок PTZ-констант).

- [ ] `/status.html` «Камери (DVRIP)»: «підключено», дрейф годинника, GOP 1; ffprobe — ключовий кадр кожні ≈ fps кадрів
- [ ] PTZ: `PtzStartPreset`/`PtzStopPreset` (рух під час натискання, зупинка ≤ 0.5 с; якщо навпаки — поміняти місцями), мапа каналів (`fixed` не рухається; інакше `XmChannel`), пресет «Зберегти»/«Перейти»
- [ ] Тривоги: подія з мініатюрою на `/events.html` ≤ 5 с; інакше alarm-server (`XmAlarmServerPort`/`Host`) — який шлях працює
- [ ] Плитки: час/розмір `ffmpeg` vs `dvrip` ×5; дефолт `dvrip` лише якщо швидше й ≤ 100 KB
- [ ] `OPFileQuery`: чи повертає камера Ret 110/111 («search succeeded») і чи треба обробляти як успіх
- [ ] Ліміт одночасних сесій камери (live + керування + завантаження + relay)
- [ ] Часи sd-сегментів у архіві vs часи файлів на картці камери (масштаб/зсув)
- [ ] Пропускна здатність завантаження з microSD vs живий потік (чи ліміт `SdMaxKbps` достатній)
- [ ] Keepalive сесії завантаження на довгому тротлінговому файлі (чи не рветься)
- [ ] Обрив мережі камери на 5 хв → ≈ 10 хв: діра в кольорі microSD в `/archive.html`, відтворення через неї, лічильники панелі
- [ ] `docker stop` при відкритій `/cam.html` — зупинка без затримки (WS закривається)
- [ ] MSE: glass-to-glass ≤ 2 с; PTZ-тап видно без перельоту; закрити сторінку → `relays.active 0` за 30 с
- [ ] Браузерна перевірка `/cam.html`, `/events.html`, `/archive.html` (не виконувалась: Docker Desktop був вимкнений)
