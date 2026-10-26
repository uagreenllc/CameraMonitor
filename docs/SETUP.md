# Встановлення CameraMonitor

## 1. Сервер

- Ubuntu 24.04, встановлені Docker Engine і плагін compose ([інструкція](https://docs.docker.com/engine/install/ubuntu/)).
- Великий HDD змонтуйте так, щоб каталог `deploy/data` лежав на ньому (наприклад, змонтувати диск у `/srv/cctv` і клонувати репозиторій туди, або зробити `deploy/data` точкою монтування).
- Контейнер працює від користувача `app` (uid 1654), тож каталог даних має належати йому:

```bash
sudo mkdir -p deploy/data && sudo chown -R 1654:1654 deploy/data
```

## 2. Мережа

- Камери — в окремій підмережі/VLAN **без маршруту в інтернет**.
- Задайте камерам статичні IP або DHCP-резервації на роутері.
- Після відрізання хмари застосунок iCSee перестане працювати. Це задумано: перегляд і архів — лише через CameraMonitor.

## 3. Камери (одноразово, до відрізання хмари)

У застосунку iCSee для кожної камери:

1. Змінити пароль користувача `admin`. Пароль **без** символів `& ? # / : @` і пробілу (він потрапляє в RTSP-URL).
2. У розширених налаштуваннях увімкнути RTSP та ONVIF.

## 4. Конфігурація

```bash
mkdir -p deploy/config && cp deploy/cameras.example.json deploy/config/cameras.json
cp -r deploy/secrets.example deploy/secrets
echo 'ПАРОЛЬ_КАМЕР' > deploy/secrets/cam_password   # без зайвих пробілів
sudo chown 1654:1654 deploy/secrets/cam_password && sudo chmod 600 deploy/secrets/cam_password
```

Контейнер працює від uid 1654, тож без `chown` він не прочитає файл із правами 600 і не запуститься.

Усі поля лежать усередині об'єкта-обгортки `Cameras` (див. `deploy/cameras.example.json`). Сервіс сам перечитує файл приблизно за 5 с після збереження — перезапуск контейнера не потрібен.

Поля `deploy/config/cameras.json`:

- `DefaultPasswordFile` — файл із паролем за замовчуванням (у контейнері `/run/secrets/cam_password`, не змінювати).
- `Items[].Id` — унікальний ідентифікатор камери (латиниця, використовується в шляхах архіву).
- `Items[].Name` — назва в інтерфейсі.
- `Items[].Host` — IP камери.
- `Items[].User` — користувач (`admin`).
- `Items[].Vendor` — виробник/профіль RTSP-URL (`xm` для Xiongmai).
- `Items[].DvripPort` — порт DVRIP (за замовчуванням `34567`).
- `Items[].Lenses[]` — об'єктиви/канали: `Id`, `Channel` (номер каналу), `Ptz: true` для поворотного, `XmChannel` — номер каналу в DVRIP, якщо він не дорівнює `Channel - 1`.

При запуску з `dotnet run` (розробка) конфіг камер лежить у `deploy/cameras.dev.json` (див. `appsettings.Development.json`), а файл із паролем задає `Cameras:DefaultPasswordFile`. Файли з реальними IP і паролями у git не комітяться (`deploy/config/` в `.gitignore`).

Пароль камер лежить лише у `deploy/secrets/cam_password`; у git і в образ він не потрапляє.

## 5. Запуск

```bash
docker compose -f deploy/docker-compose.yml up -d --build
docker compose -f deploy/docker-compose.yml logs -f
curl localhost:8080/health
```

## 6. Доступ

- Вебінтерфейс у локальній мережі: `http://<сервер>:8080/`.
- Ззовні — лише через [Tailscale](https://tailscale.com/download): встановіть його на сервер і на свій пристрій, власних налаштувань не потрібно.

## 7. Оновлення

```bash
git pull && docker compose -f deploy/docker-compose.yml up -d --build
```

## 8. DVRIP (Xiongmai)

Порт камери **34567** має бути доступний із сервера. Через нього агент веде керування, тривоги, знімки, PTZ і довантаження з microSD.

При кожному підключенні агент змінює налаштування камери:

- GOP = 1 с (ключовий кадр щосекунди — для швидкого старту живого відео);
- smart-encode вимкнено;
- детекція руху та людини ввімкнена;
- час камери синхронізується з серверним у часовому поясі `Server:TimeZone` (за замовчуванням `Europe/Kyiv`), щодоби.

Якщо номер каналу DVRIP не дорівнює `Channel - 1`, задайте `LensConfig.XmChannel` в об'єктиві.

**Тривоги не приходять?** Деякі прошивки не надсилають тривоги в DVRIP-сесію. Тоді в `deploy/docker-compose.yml` (секція `environment`) задайте:

```yaml
Server__XmAlarmServerPort: "15002"
Server__XmAlarmServerHost: "<LAN IP сервера>"
```

і перезапустіть. Порт слухається лише на host-мережі (`network_mode: host`, уже так).

**Плитки.** `Server:TileSource` — `ffmpeg` (за замовчуванням) або `dvrip` (знімок із камери).

## 9. Довантаження з microSD

У кожній камері має бути microSD. Коли запис сервера має діру (обрив мережі, перезапуск), агент завантажує відповідні файли з картки, переформатовує їх і додає в архів; на шкалі часу такі ділянки мають свій колір.

- `Server:SdLoopDays` (3) — на скільки днів назад шукати діри; ≈ тривалість запису на картці.
- `Server:SdMinGapSeconds` (120) — коротші діри не довантажуються.
- `Server:SdMaxKbps` (8000) — ліміт швидкості завантаження, кбіт/с; `0` — без ліміту.

Стан черги — на `/status.html` (панель довантаження).

## 10. Живе відео вибраної камери

Сторінка камери (`/cam.html`) показує відео з малою затримкою через WebSocket `/ws/live` (MSE). `Server:MaxRelays` (4) — скільки таких ретрансляцій одночасно; глядачі вже запущеної не рахуються. Браузери без MSE автоматично отримують HLS.

## Буде в наступних планах

- Автопошук камер через ONVIF
- Сповіщення в Telegram
- Вхід за паролем
