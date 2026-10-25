# CameraMonitor — внутрішнє відеоспостереження UA Green: дизайн

Схвалено 2026-10-25. Джерело: сесія брейнстормінгу + рев’ю стороннього інженера (субагент).

Дата: 2026-10-25. Після схвалення цей документ копіюється в `docs/superpowers/specs/2026-10-25-camera-monitor-design.md` проєкту.

## Контекст

У компанії є ~10 камер типу iCSee (прошивка Xiongmai/XMEye): дві лінзи 4+4 МП (одна фіксована, одна поворотна PTZ), H.264/H.264+, Wi-Fi + RJ45, microSD, 12 В. Застосунок iCSee показує одну камеру за раз, архів тримає на microSD і в китайській хмарі, спільного екрана й власних правил тривог не має.

Потреба: власна внутрішня система відеонагляду: один екран з усіма камерами, архів на власному сервері в офісі, автоматичні сповіщення. Це інструмент для себе, не частина продукту Oberig.

Рішення користувача: писати все самостійно (не Frigate/ZoneMinder), окремий проєкт, стек .NET; якщо потрібна бібліотека, яка існує лише в іншій мові, копіюємо/портуємо її в проєкт.

## Цілі

1. Живий перегляд усіх камер (обидві лінзи) на одній сторінці браузера з офісної мережі та віддалено через VPN. Для вибраної камери затримка ≤ 2 с, щоб PTZ керувався «вживу» (рішення користувача).
2. Безперервний запис 24/7 кожного основного потоку на диск сервера з налаштовуваним терміном зберігання; перегляд архіву по камері й часу; експорт фрагмента в MP4.
3. Тривоги в Telegram: людина в кадрі в неробочий час (детекція самої камери), камера зникла/потік завис, диск майже повний, запис зупинився.
4. Керування камерами: список камер у конфігу, автопошук нових по ONVIF, PTZ для поворотної лінзи з UI, синхронізація часу.
5. Працює без нагляду: один `docker compose up` на офісній Linux-машині, переживає перезавантаження, сам перепіднімає потоки.
6. Довантаження розривів архіву з microSD камер (рішення користувача, 2026-10-25): коли камера повертається після offline/зависання, агент DVRIP запитує її локальні записи за вікно розриву (`OPFileQuery`), скачує їх по DVRIP, ремуксує у наші 10-с TS-сегменти в ту ж структуру каталогів і сам вписує рядки в індекс з правильним `StartUtc` і `Source = "sd"` (нова колонка окремою міграцією). На таймлайні такі ділянки показуються іншим кольором. Вимоги: microSD у кожній камері; заповнити можна лише розриви молодші за період перезапису картки (~3–5 днів на 128 ГБ при 4+4 МП). `DirectoryScanner` ніколи не датує довантажені файли за mtime: довантаження індексує їх саме, до того як мине `MinAge` сканера.

## Не-цілі (v1)

- Не продукт і не SaaS: один тенант, один пароль оператора, без ролей.
- Без мобільного застосунку: адаптивна веб-сторінка достатня.
- Без серверного AI: використовуємо детекцію людини самої камери. Залишаємо точку розширення для ONNX/YOLO пізніше.
- Без двостороннього аудіо, без запису звуку (можна ввімкнути прапорцем, бо ffmpeg копіює і аудіо).
- Без хмари й без відкритих портів в інтернет.
- Без субпотоків: сітка показує JPEG-кадри, живе відео лише для вибраної камери (див. ризики).
- Інші виробники камер: будь-яка RTSP-камера дає live+запис; ONVIF дає PTZ/пошук; тривоги через DVRIP лише для Xiongmai.

## Анти-цілі (провал, навіть якщо технічно працює)

- Відео залишає мережу компанії (хмара, публічні порти, незашифрований віддалений доступ).
- Дірки в архіві, яких ніхто не помітив: потік тихо помер, диск переповнився. Детекція зависання і тривога про це обов'язкові.
- Система, яку треба няньчити: ручні рестарти, ручне чищення диска.
- Перекодування відео на сервері (20 потоків спалять CPU): тільки stream copy.

## Обмеження та вхідні дані

**Камери.** Порти Xiongmai: RTSP 554, ONVIF 8899, DVRIP 34567. RTSP-шлях (запасний, якщо ONVIF GetStreamUri не дав): `rtsp://host:554/user=U&password=P&channel=N&stream=0.sdp?real_stream` (основний), `stream=1.sdp` (субпотік). Дволінзова камера: канали 1 і 2. У свіжих прошивках RTSP/ONVIF можуть бути вимкнені: вмикаються в iCSee (Advanced settings) один раз на камеру. Заводський пароль admin порожній: під час налаштування обов'язково ставимо надійний. Паралельних RTSP-клієнтів камера тримає обмежено (~3–4 на канал), тому кожен потік тягнемо з камери рівно один раз.

**Мережа.** Хмару iCSee відрізаємо з першого дня (рішення користувача): камери дротом RJ45 (Wi-Fi для 20 потоків ненадійний) в окрему підмережу/VLAN без маршруту в інтернет, статичні IP або DHCP-резервації; застосунок iCSee після цього не працює, що й задумано. Наслідок: камери не бачать NTP, тож час на них щодня виставляє наш DVRIP-агент (інакше таймлайн і підписи на кадрах розійдуться). Сервер в тій же мережі або з маршрутом до неї. Віддалений доступ лише через Tailscale (або WireGuard) на хості сервера. Усі 10 камер однієї моделі, одна з них стоїть на столі розробника з першого дня (підтверджено користувачем), тому DVRIP-агент обов'язковий для всіх, а пробінг прошивки іде з M1.

**Сервер.** Linux-ПК в офісі з Docker Engine (рішення користувача): усі припущення про host network, tmpfs і локальний диск чинні.

**Об'єм.** 10 камер × 2 лінзи = 20 основних потоків (по одному ffmpeg на кожен, субпотоки не тягнемо). Основний потік 4 МП H.264 ≈ 2–4 Мбіт/с (режим H.264+ зі «розумним» довгим GOP вимикаємо, див. ризики, тому беремо верхню межу). При 3 Мбіт/с:

| | на потік | 20 потоків |
|---|---|---|
| за добу | 32 ГБ | 650 ГБ |
| 7 днів | | 4,5 ТБ |
| 14 днів | | 9 ТБ |
| 30 днів | | 19,5 ТБ |

Термін зберігання задається як «N днів або поріг заповнення диска, що настане раніше», отже розмір диска визначає реальний термін. Сервер: 4+ ядра, 8 ГБ RAM, 1 GbE, HDD під архів (CMR, surveillance-клас) + SSD під ОС і live-сегменти (або tmpfs). CPU навантаження низьке, бо без перекодування.

**Стек (рішення користувача: .NET; деталі мої).**

- .NET 9 (`Nullable`, `ImplicitUsings`, `sealed` + primary constructors, file-scoped namespaces, як у TTApp/Oberig), одне ASP.NET Core-застосування `src/CameraMonitor.Api` у стилі TTApp (vertical slices `Features/*`, minimal API, `BackgroundService`-и) + `tests/CameraMonitor.Tests` (xUnit + FluentAssertions + NSubstitute, `WebApplicationFactory<Program>`). Поділ на кілька проєктів не робимо, доки не з'явиться друга збірка, якій потрібен спільний код.
- Логи: Serilog (`ReadFrom.Configuration`, компактний JSON у консоль, `UseSerilogRequestLogging`), як у всіх трьох репо. Конфіг: `AddOptions<T>().Bind().Validate().ValidateOnStart()`.
- Фонові сервіси: шаблон `GospodarSyncService` з Oberig (цикл `while (!stoppingToken...)`, окремий `catch (OperationCanceledException)`, експоненційний backoff у статичному тестованому `NextDelay`, виняток ніколи не виходить з `ExecuteAsync`, бо інакше впаде весь хост).
- ffmpeg: єдина медіа-залежність, пакет з apt у Docker-образі, керується через `System.Diagnostics.Process` (у ваших репо такого коду ще нема, це новий шаблон).
- Сховище індексу: SQLite через EF Core 9 `Microsoft.EntityFrameworkCore.Sqlite` (команда всюди на EF, міграції `MigrateAsync()` при старті). 3–4 сутності. Файл у `/data/db/`. Запис із індексатора, читання з API: одне з'єднання на операцію, WAL-режим.
- ONVIF: власний мінімальний SOAP-клієнт на `HttpClient` + `XDocument` (WS-Security UsernameToken PasswordDigest). Операцій ~8, WSDL-генерація не потрібна.
- DVRIP (Xiongmai «Sofia»): порт `OpenIPC/python-dvr` (MIT, `dvrip.py` + `asyncio_dvrip.py`; має login/sofia_hash, alarmStart, get/set_detect_info, snapshot, set_time, reboot) у C#: `DvripClient` з логіном, keepalive, каналом тривог, get/set конфігу, знімком, синхронізацією часу. Вихідний Python тримаємо в `third_party/python-dvr/` як специфікацію й для фікстур.
- Telegram: Bot API напряму через `HttpClient`, за зразком `TelegramSender` з TTApp (ніколи не кидає, повертає Sent/Blocked/Failed, `AddStandardResilienceHandler`, короткий таймаут; у TTApp є коментар, що api.telegram.org в Україні часто повільний або блокується, тому в нас черга з повторами). Додаємо `sendPhoto` (multipart), якого в ваших репо ще нема. Telegram.Bot NuGet не беремо, бо команд бот не приймає.
- Фронтенд: статичні HTML/CSS/JS у `wwwroot/` + вендорований `hls.js`. Без збірки, без фреймворку. Українська мова UI, темна тема.
- Конфіг: `appsettings.json` + `cameras.json` (strongly-typed Options, `reloadOnChange`), секрети як у Oberig: файли в `./secrets/` примонтовані read-only у `/run/secrets/`, читаються через ключі `*File` (`Cameras:PasswordFile`, `Telegram:BotTokenFile`), або `Section__Key` зі змінних середовища `.env`.
- Деплой: Dockerfile (multi-stage `sdk:9.0` → `aspnet:9.0`, спочатку копіюються `.csproj` для кешу restore, apt `ffmpeg tzdata curl`, користувач `app` без root, тому томи з правами для uid `app`), `docker-compose.yml` з `restart: unless-stopped`, healthcheck `curl /health`, json-file-логи з лімітом, томи `/data` (HDD) і tmpfs `/run/live`. Відхилення від вашої звички: `network_mode: host` замість bridge + nginx, бо WS-Discovery потребує multicast і сервіс живе лише в LAN; nginx перед Kestrel для роздачі HLS: точка розширення, якщо глядачів стане більше за 3–5.
- Tooling: `.editorconfig` і `.githooks` (format, build, test) копіюємо з TTApp, GitHub Actions `dotnet format --verify-no-changes` + build + test, Conventional Commits.

## Архітектура

Один процес. Компоненти (кожен `BackgroundService` або сервіс всередині):

```
cameras.json ─► CameraRegistry ─┬─► StreamSupervisor ──► ffmpeg ×20 (одна лінза = один процес)
                                │        │ tee → /data/rec/<cam>/<lens>/<YYYYMMDD>/<HH>/<HHMMSS>.ts (10 c)
                                │        │     + /run/live/<cam>/<lens>/*.ts + index.m3u8 (2 c, HLS, onfail=ignore)
                                │        └─► StreamState (out_time з -progress, bitrate, lastSegmentAt, restarts)
                                ├─► SegmentIndexer (від супервізора: сегмент готовий, коли відкрився наступний) ──► SQLite.segments
                                ├─► RetentionService (видаляє старе, стежить за диском)
                                ├─► XmCameraAgent (DVRIP: логін, keepalive, тривоги, знімок, PTZ, час, GOP-конфіг) ──► SQLite.events
                                ├─► TileSnapshotter (JPEG кожні 1–2 с на лінзу для сітки: DVRIP snapshot, запас: ffmpeg з live-сегмента)
                                ├─► LowLatencyRelay (на вимогу: ffmpeg rtsp→fMP4 у stdout ──► WebSocket ──► MSE у cam.html, ≤ 2 с)
                                ├─► OnvifClient (лише discovery + GetStreamUri, з поправкою на зсув годинника)
                                └─► AlertEngine (правила + розклад) ──► TelegramSender
Web (minimal API + wwwroot): grid (JPEG-плитки), cam (live HLS + PTZ), archive, events, status, login
```

### StreamSupervisor

- Для кожної лінзи запускає один ffmpeg і наглядає за ним (одна команда, два виходи через `tee`):
  ```
  ffmpeg -nostdin -rtsp_transport tcp -i <rtsp> -c copy -map 0 -progress pipe:1 \
    -f tee "[f=segment:segment_time=10:strftime=1:segment_format=mpegts]/data/rec/<cam>/<lens>/%Y%m%d/%H/%H%M%S.ts|\
            [f=hls:hls_time=2:hls_list_size=6:hls_flags=delete_segments+independent_segments:onfail=ignore]/run/live/<cam>/<lens>/index.m3u8"
  ```
  `onfail=ignore` на live-виході: якщо HLS-гілка впаде, запис триває. Якщо `tee` виявиться крихким на реальній камері, запасний варіант: два процеси на лінзу (тягне камеру двічі), вирішує будівельник.
- **GOP.** При `-c copy` сегменти різати можна лише на I-кадрах, а Xiongmai у режимі H.264+ ставить GOP 4–10 с: сегменти по 2 с не вийдуть, затримка виросте до 15–30 с. Тому `XmCameraAgent` при старті виставляє через DVRIP інтервал I-кадрів = fps (GOP 1 с) і вимикає smart-encode; для не-Xiongmai камер це робиться в їхньому веб-інтерфейсі (у SETUP.md).
- Живість потоку береться з `-progress pipe:1` (`out_time` росте), а не з появи сегментів. Нема прогресу 10 с → kill → рестарт з експоненційною затримкою (1 с → 60 с) → після 60 с без відео камера позначається `offline` і створюється подія `camera_offline`; повернення → `camera_online`.
- Зміна `cameras.json` (reloadOnChange) → додані/видалені/змінені потоки перезапускаються, решта не чіпаються.

### Живе відео вибраної камери з малою затримкою

- HLS із 2-с сегментами дає 5–10 с від об'єктива до екрана, для PTZ цього мало. Тому `cam.html` отримує відео через `LowLatencyRelay`: при відкритті сторінки сервер запускає окремий ffmpeg `-rtsp_transport tcp -i <rtsp> -c copy -f mp4 -movflags frag_keyframe+empty_moov+default_base_moof -min_frag_duration 100000 pipe:1` (ще одне RTSP-з'єднання до камери, лише на час перегляду), читає stdout, розбирає fMP4 на box-и (`ftyp`+`moov` кешує як init-сегмент, далі пари `moof`+`mdat`) і розсилає всім WebSocket-клієнтам цієї лінзи; новий клієнт отримує init-сегмент і починає з наступного `moof`. У браузері `MediaSource` + `SourceBuffer` в режимі `sequence`, буфер тримаємо ~0,3 с. Очікувана затримка 0,5–1,5 с у LAN при GOP = 1 с.
- Процес зупиняється через 30 с після відключення останнього клієнта. Одночасно не більше 4 таких реле (ліміт у конфігу), бо кожне тягне камеру ще раз.
- Запас: HLS-плеєр на тій самій сторінці для браузерів без MSE для H.264 fMP4 (старий iOS Safari) і для архіву. WebRTC не беремо: ICE/DTLS/SRTP у .NET це тижні роботи заради зайвого ~0,5 с.

### Архів і відтворення

- MPEG-TS сегменти по 10 с у каталогах по годинах (`<YYYYMMDD>/<HH>/`): `hls.js` грає TS напряму, тож і live, і архів працюють через той самий плеєр; перемотка коштує один файл ~4 МБ, прийнятно й через Tailscale з телефона. Файлів: ~170 тис./добу на 20 потоків, 2,4 млн за 14 днів, для ext4 з погодинними каталогами нормально. Таймстемпи не скидаємо між файлами (без `reset_timestamps`), тож `#EXT-X-DISCONTINUITY` потрібен лише на справжніх розривах і рестартах ffmpeg.
- `SegmentIndexer` не стежить за файловою системою (FileSystemWatcher у Docker ненадійний): супервізор знає, що сегмент завершений, коли ffmpeg відкрив наступний (`-progress`/stderr), плюс опитування каталогів кожні 10 с як страховка і для індексації після рестарту сервісу. Пише в `segments(cam, lens, start_utc, duration_ms, path, bytes, discontinuity)`.
- `GET /archive/{cam}/{lens}.m3u8?from=&to=` → синтезований VOD-плейлист із рядків таблиці. `GET /seg/...` віддає файл. Таймлайн: `GET /api/timeline?cam=&day=` → інтервали запису + події.
- Експорт: `GET /api/export?cam=&lens=&from=&to=` → `ffmpeg -f concat -safe 0 -i list.txt -c copy out.mp4`, стрімиться в відповідь, обмеження 2 години на запит.
- `RetentionService` кожні 5 хв: видаляє найстарші сегменти, поки `age > retention_days` або `free < min_free_gb`; чистить порожні каталоги й рядки БД. Якщо фактичний термін зберігання впав нижче `min_retention_days` → подія `retention_short`.

### Камери: ONVIF і DVRIP

- `OnvifClient` (порт 8899) навмисно маленький: WS-Discovery Probe (multicast; запас: unicast Probe або сканування портів підмережі камер) для списку пристроїв (сторінка «Статус» показує «нові камери, яких нема в конфігу») і `GetProfiles` + `GetStreamUri` для RTSP-URL кожної лінзи (з fallback на шаблон Xiongmai). Перед підписаним запитом викликає неавтентифікований `GetSystemDateAndTime` і зсуває `Created` у дайджесті, бо Xiongmai відкидає дайджест при розбіжності годинників. PTZ, знімок і час для Xiongmai робимо через DVRIP, ONVIF-варіанти цих операцій лише як запас для чужих камер.
- `XmCameraAgent` (порт 34567) на камеру: одне TCP-з'єднання, логін (`sofia_hash` пароля), keepalive кожні 20 с, підписка на тривоги (`GUARD_REQ` → `ALARM_REQ` з `AlarmInfo{Channel, Event: VideoMotion|HumanDetect|..., Status: Start|Stop}`), при старті перевіряє/вмикає `Detect.HumanDetection`/`MotionDetect`, виставляє GOP = fps і вимикає smart-encode, синхронізує час. PTZ (`OPPTZControl`) і знімок (`snapshot`) теж тут. Якщо прошивка не шле тривог на 34567 (буває на свіжих iCSee), запас: ONVIF PullPoint або націлити вбудовану в камеру відправку тривог e-mail/FTP на крихітний SMTP/FTP-приймач у нашому процесі (знімок приходить разом). Кожна тривога → `events(cam, lens, type, started_utc, ended_utc, snapshot_path)`; знімок береться через DVRIP `snapshot` (є в python-dvr), запасний шлях: ONVIF `GetSnapshotUri`, далі ffmpeg з останнього live-сегмента.
- Для не-Xiongmai камер агент не запускається; тривоги руху можна пізніше брати з ONVIF PullPoint (точка розширення, не v1).

### Тривоги

- Правила в конфігу: робочий розклад (`working_hours` по днях тижня, таймзона Europe/Kyiv), для кожної камери `alertPersonOutsideHours: true|false`, `cooldownSeconds` (типово 120). Системні правила завжди ввімкнені: `camera_offline` > 60 с і повернення, `disk_high` (free < min_free_gb), `retention_short`, `stream_crashloop` (>5 рестартів за 10 хв), `recorder_stalled` (нема нового архівного сегмента > 3 хв при живому потоці).
- `TelegramSender`: `sendPhoto` зі знімком і підписом українською + посилання на архів у момент події (`/archive?cam=&t=`); при недоступності Telegram чергує і повторює, не губить. Чат-айді в конфігу; команди боту v1 не обробляє.

### Web і безпека

- Статика з `wwwroot/`: `index.html` (сітка 4×5/5×4 плиток-JPEG, що оновлюються кожні 1–2 с з `/api/tile/{cam}/{lens}.jpg`; 20 одночасних hls.js-плеєрів на одній сторінці впираються в ліміт апаратного декодування ~16 сесій і не працюють на телефонах, тому живе відео лише у вибраній плитці, опційно до 4 одночасно), `cam.html` (живе відео через WebSocket/MSE з HLS-запасом, PTZ-джойстик і пресети, таймлайн останніх 24 г), `archive.html` (камера, день, таймлайн з інтервалами й маркерами подій, швидкість, вибір діапазону → експорт), `events.html` (список з мініатюрами, фільтри), `status.html` (на потік: fps/бітрейт/вік сегмента/рестарти; диск; фактичний термін зберігання; знайдені ONVIF-пристрої).
- Вхід: один пароль оператора (хеш у конфігу) → ASP.NET cookie-auth, HttpOnly, обмеження спроб входу вбудованим rate limiter (як у TTApp). Відхилення від ваших JWT-бекендів свідоме: фронтенд це статичний HTML, а hls.js тягне сегменти звичайними GET-ами, тож cookie простіше за токен у заголовках. Усі API і медіа за авторизацією. HTTP у LAN, зовні тільки через Tailscale (вже шифрує); TLS на Kestrel: точка розширення.
- Паролі камер і токен бота не потрапляють у логи й у відповіді API.

### Структура репозиторію

```
CameraMonitor/
  CameraMonitor.sln  .editorconfig  .githooks/  .github/workflows/ci.yml
  src/CameraMonitor.Api/
    Program.cs
    Features/Cameras/   (конфіг, реєстр, ONVIF discovery endpoint)
    Features/Streams/   (StreamSupervisor, FfmpegProcess, StreamState, live HLS endpoints, LowLatencyRelay + WebSocket endpoint, Fmp4BoxReader)
    Features/Archive/   (SegmentIndexer, RetentionService, playlist/timeline/export endpoints)
    Features/Events/    (XmCameraAgent, events endpoints)
    Features/Alerts/    (Schedule, AlertEngine, TelegramSender)
    Features/Auth/      (login endpoint, cookie)
    Infrastructure/     (Onvif/OnvifClient, Xm/DvripClient, Db/AppDbContext + Migrations)
    wwwroot/            (index, cam, archive, events, status, login .html + app.css + hls.min.js)
  tests/CameraMonitor.Tests/
  third_party/python-dvr/       оригінал як специфікація (MIT)
  deploy/                       Dockerfile, docker-compose.yml, compose.dev.yml (mediamtx-симулятор камер), cameras.example.json, secrets.example/
  docs/                         SETUP.md (мережа, iCSee, паролі, Tailscale), superpowers/specs/, superpowers/plans/
```

## Ризики й запасні варіанти (з рев'ю стороннього інженера, вже враховано вище)

| Ризик | Що зроблено в дизайні | Запас |
|---|---|---|
| Довгий GOP H.264+ ламає 2-с HLS і детекцію зависання | GOP=fps через DVRIP; живість з `-progress` | ручне налаштування GOP у веб-UI камери |
| 60-с TS-сегменти: повільна перемотка, discontinuity щохвилини | 10-с сегменти, без скидання PTS | 15-с сегменти |
| 20 hls.js на сторінці | JPEG-плитки, live лише для вибраної | до 4 live-плиток |
| Прошивка не шле DVRIP-тривог / HumanDetect = VideoMotion | агент перевіряє конфіг детекції при старті | ONVIF PullPoint; SMTP/FTP-приймач тривог камери |
| Xiongmai-ONVIF: зсув годинника, дивні URI, ігнорує PTZ Stop | ONVIF лише discovery + StreamUri; PTZ/знімок/час через DVRIP | шаблон RTSP-URL |
| `tee` падає цілком, якщо падає один вихід | `onfail=ignore` на live | два процеси на лінзу |
| Linux-залежності (host network, tmpfs, SQLite на локальному диску) | вимога: Linux Docker Engine, `/data` на локальному диску | unicast discovery; каталог замість tmpfs |
| HLS занадто повільний для PTZ | окреме реле fMP4→WebSocket→MSE для вибраної камери | HLS з `hls_time=1`, ~3–4 с |
| Довантаження з microSD: формат файлів камери (XM .h264/.mp4), швидкість скачування по DVRIP, нема картки | ремукс через ffmpeg; скачування по одному файлу з обмеженням швидкості; без картки розрив лишається поміченим | лише позначати розриви (попередня поведінка) |

## Що лишається будівельнику

Точні прапорці ffmpeg і `tee` vs два процеси; схема SQLite (EF-сутності); розкладка UI; повнота порту DVRIP (мінімум: логін, keepalive, тривоги, знімок, PTZ, час, get/set конфігу); джерело JPEG-плиток (DVRIP snapshot чи ffmpeg з live-сегмента, за результатом заміру на реальній камері); розмір live-буфера hls.js.

## План реалізації (етапи, кожен закінчується робочою перевіркою)

**M0. Скелет.** `dotnet new` web + xunit, sln, `.editorconfig`, `.gitignore`, Dockerfile з ffmpeg, `docker-compose.yml`, `compose.dev.yml` з `bluenviron/mediamtx` + ffmpeg-паблішером `testsrc` як 2 «камери», `cameras.example.json`, README. Перевірка: `docker compose -f deploy/compose.dev.yml up` і `dotnet test` зелені.

**M1. Потоки й live.** `Config` (Options + reloadOnChange), `StreamSupervisor` + `FfmpegProcess`, `StreamState`, `/api/cameras`, `/live/{cam}/{lens}/...`, `TileSnapshotter` (поки через ffmpeg з live-сегмента), `index.html` (JPEG-сітка) + `cam.html` з hls.js, базовий `status.html`. Перевірка: сітка показує 2 симульовані камери; вбити паблішер → через ≤60 с `offline` на статусі, повернути → `online`. Паралельно на реальній камері зі столу: пробінг RTSP-шляхів, мапінг лінза↔канал, GOP.

**M2. Архів.** `SegmentIndexer`, EF Core SQLite, `RetentionService`, синтез VOD-плейлиста з discontinuity, `/api/timeline`, експорт, `archive.html`. Перевірка: записати 10 хв, відкрити архів, промотати, експортувати 2 хв у MP4 і відкрити у плеєрі; виставити `retention` 5 хв і побачити видалення.

**M3. DVRIP.** `DvripClient` (порт python-dvr), `XmCameraAgent`: логін, keepalive, GOP/детекція-конфіг, час, тривоги → таблиця `events`, знімок (переводимо `TileSnapshotter` на нього, якщо швидший), PTZ → джойстик і пресети у `cam.html`, `events.html`. Тести на фікстурах пакетів (кадрування заголовка, `sofia_hash` з відомими векторами з python-dvr). Перевірка на реальній камері: поворот, пресет, помахати → подія з мініатюрою за ≤5 с.

**M3b. Довантаження з microSD.** Міграція `Source` у `Segments`; `SdBackfill`: на подію `camera_online` після розриву → `OPFileQuery` за вікно розриву → скачування по DVRIP → ffmpeg-ремукс у 10-с TS у `rec/<cam>/<lens>/<yyyyMMdd>/<HH>/` → рядки з правильним `StartUtc`, `Source="sd"`; таймлайн малює `sd` іншим кольором; статус показує чергу довантаження. Перевірка на реальній камері: відключити кабель на 5 хв, під'єднати → за кілька хвилин розрив на таймлайні заповнюється кольором «з картки», відтворення працює.

**M4. Живе відео ≤ 2 с.** `LowLatencyRelay`, `Fmp4BoxReader`, WebSocket endpoint `/ws/live/{cam}/{lens}`, MSE-плеєр у `cam.html` з HLS-запасом, ліміт реле. Тести: розбір box-ів на фікстурі fMP4, пізнє приєднання клієнта отримує init + стартує з `moof`. Перевірка на реальній камері: затримка ≤ 2 с (годинник у кадрі), PTZ-тап і відгук у кадрі без «перельоту»; закрити сторінку → ffmpeg реле зникає через 30 с.

**M5. ONVIF.** WS-Discovery (multicast + unicast-запас), `GetProfiles`/`GetStreamUri` із поправкою на зсув годинника; список знайдених пристроїв у статусі; RTSP-URL з ONVIF замість шаблону. Перевірка: нова камера в мережі з'являється на сторінці статусу; URL з ONVIF збігається з робочим.

**M6. Тривоги.** Розклад, `AlertEngine`, системні правила, `TelegramSender` з чергою і `sendPhoto`. Перевірка: подія в неробочий час → фото в тестовий чат; відключити камеру → повідомлення; заповнити диск (маленький `min_free_gb`) → повідомлення.

**M7. Доведення.** Cookie-auth і rate-limit, `/health`, структуровані логи без секретів, `docs/SETUP.md` (VLAN без інтернету, iCSee: ввімкнути RTSP/ONVIF і змінити пароль до відрізання хмари, Tailscale), чекліст розгортання на 10 камер, прибирання `ponytail:`-коментарів у боргову відомість.

## Перевірка (end-to-end)

1. Dev: `docker compose -f deploy/compose.dev.yml up` → http://localhost:8080 → сітка з 2 тестовими камерами, архів пишеться в `./data/rec`, зупинка паблішера дає `camera_offline` в Telegram-тест-чаті.
2. Реальна камера: ввімкнути RTSP/ONVIF в iCSee, додати в `cameras.json` → обидві лінзи в сітці, PTZ рухає нижню лінзу, рух перед камерою дає подію зі знімком.
3. Навантаження: 10 камер (або 20 симульованих потоків) добу: CPU < 2 ядер, 0 розривів у таймлайні, диск росте згідно з таблицею об'єму.
4. `dotnet test`: кодек DVRIP, SOAP-дайджест, синтез плейлиста (з discontinuity), планувальник ретеншену, розклад тривог, `NextDelay` backoff.
5. Затримка live у `cam.html` на реальній камері ≤ 6 с після виставлення GOP=fps; PTZ-тап і реакція в кадрі.

## Рішення користувача (з розмови)

- Писати все самостійно, окремий проєкт `D:\Work\CameraMonitor`, стек .NET; бібліотеки портувати в проєкт, якщо треба.
- ~10 камер однієї моделі, одна на столі розробника з першого дня.
- Один екран з усіма камерами, архів на своєму сервері, автоматичні тривоги.
- Живе відео вибраної камери ≤ 2 с (PTZ «вживу»).
- Хмару iCSee відрізаємо з першого дня.
- Сервер: Linux-ПК в офісі.
- Довантажувати розриви з microSD камер (додано під час виконання плану A).

## Мої рішення, які варто перевірити під час читання

- Термін зберігання за замовчуванням 14 днів або поріг диска (`min_free_gb` 100), що настане раніше: параметр у конфігу, розмір HDD підбираєш за таблицею об'єму.
- Тривоги в Telegram (у вас уже є бот-інфраструктура в TTApp); канал змінний, Viber/Господар не в v1.
- Робочий розклад за замовчуванням пн–пт 08:00–19:00, Europe/Kyiv; редагується в конфігу.
- Камери дротом RJ45; Wi-Fi не забороняємо, але не рекомендуємо.
- Один пароль оператора, cookie-auth, HTTP у LAN, зовні лише Tailscale.
- `network_mode: host` без nginx; nginx перед Kestrel лише якщо глядачів стане більше за 3–5.
- EF Core SQLite замість сирого SQL, бо команда всюди на EF.
