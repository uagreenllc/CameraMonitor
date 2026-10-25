# CameraMonitor

Внутрішній сервіс відеоспостереження (CCTV/NVR) для ~10 IP-камер Xiongmai/iCSee: ASP.NET Core (.NET 9) приймає RTSP-потоки камер, веде запис і віддає живе відео та архів у вебінтерфейсі.

## Розробка

```bash
# імітатор камер (mediamtx + 2 ffmpeg-публікатори): rtsp://localhost:8554/cam1, cam2
docker compose -f deploy/compose.dev.yml up -d
ffprobe -rtsp_transport tcp rtsp://localhost:8554/cam1

# API
dotnet run --project src/CameraMonitor.Api

# тести
dotnet test

# git-хуки
git config core.hooksPath .githooks
```

Специфікація: [docs/superpowers/specs/2026-10-25-camera-monitor-design.md](docs/superpowers/specs/2026-10-25-camera-monitor-design.md). План: [docs/superpowers/plans/2026-10-25-camera-monitor-core.md](docs/superpowers/plans/2026-10-25-camera-monitor-core.md).
