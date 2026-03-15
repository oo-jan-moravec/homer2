# Rover Operator Console

Single-deployment web app: Angular frontend + .NET WebAPI backend.

- **backend/** — .NET 8 WebAPI, serves Angular from `wwwroot`
- **frontend/** — Angular 19 SPA (rover operator GUI)
- **deploy/** — systemd unit file for RPi

## Quick start

```bash
# Build (Angular → wwwroot, then dotnet publish for linux-arm64)
./build-and-deploy.sh --build

# Build + deploy to RPi (installs systemd service, requires sudo on RPi)
./build-and-deploy.sh
# Env: RPI_HOST, RPI_USER  (default: rpi-rover-brain4.local, hanzzo)
# Installs to /opt/rover-operator-console, runs as RPI_USER
```

## API (GUI-ready)

**REST**
- `GET /api/rover/status` — serial, LCD, IR, camera availability
- `GET /api/rover/telemetry` — one telemetry snapshot (fallback; use SignalR for streaming)
- `POST /api/rover/lcd` — `{ line1, line2 }` (max 16 chars each)
- `POST /api/rover/ir` — `{ on: bool }` | `POST /api/rover/ir/toggle`
- `GET /api/rover/camera` — capture JPEG
- `POST /api/rover/encoders/reset` — zero encoders
- `POST /api/rover/enc` — `{ enabled, kp?, max? }` encoder correction

**SignalR** (for driving + streaming telemetry)
- `/hubs/drive` — `Drive({ bearing, velocity })`, `Stop()`. Send every ~300ms while driving (watchdog 500ms).
- `/hubs/telemetry` — server pushes `ReceiveTelemetry` every ~250ms when not driving.

**Rover capabilities** (from test-suite, excl. ultrasound): LCD (I2C), heartbeat LED, telemetry, drive, IR LED, camera.

## Local development

Terminal 1 — backend:
```bash
cd backend && dotnet run
# API: http://localhost:5102, SignalR: /hubs/drive, /hubs/telemetry
```

Terminal 2 — frontend (proxies /api to backend):
```bash
cd frontend && npm start
# UI: http://localhost:4200
```
