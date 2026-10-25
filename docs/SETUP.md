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
- `Items[].Lenses[]` — об'єктиви/канали: `Id`, `Channel` (номер каналу), `Ptz: true` для поворотного.

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

## Буде в наступних планах

- PTZ-керування
- Тривоги та сповіщення в Telegram
- Автопошук камер через ONVIF
- Довантаження записів з microSD камери
- Вхід за паролем
