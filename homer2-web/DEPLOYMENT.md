# Homer2 Web Deployment Guide

## Quick Start

Deploy to rpi-rover-brain4:

```bash
cd homer2-web
pwsh deploy-to-rpi.ps1
```

Or with PowerShell Core:

```powershell
./deploy-to-rpi.ps1
```

## Configuration

### Using Parameters:
```powershell
./deploy-to-rpi.ps1 -RpiUser "hanzzo" -RpiHost "rpi-rover-brain4.local" -RpiDeployDir "/home/hanzzo/homer2-web" -RpiArch "linux-arm64"
```

### Using Environment Variables:
```bash
export RPI_USER="hanzzo"
export RPI_HOST="rpi-rover-brain4.local"
export RPI_DEPLOY_DIR="/home/hanzzo/homer2-web"
export RPI_ARCH="linux-arm64"
pwsh deploy-to-rpi.ps1
```

### Defaults:
- **User:** hanzzo
- **Host:** rpi-rover-brain4.local
- **Deploy Dir:** /home/hanzzo/homer2-web
- **Architecture:** linux-arm64 (use linux-arm for 32-bit RPi OS)

## Prerequisites

### On Your Development Machine:
- PowerShell Core (pwsh) 7.0 or later
- .NET 10.0 SDK
- SSH access to the Raspberry Pi (key-based auth recommended)
- rsync

### On Your Raspberry Pi:
- SSH enabled
- I2C enabled (for LCD): `sudo raspi-config` → Interface Options → I2C
- 64-bit RPi OS for linux-arm64 (check with `uname -m` → aarch64)

## What the Script Does

1. Checks SSH connectivity to the Raspberry Pi
2. Builds the .NET app for ARM64
3. Publishes as self-contained deployment
4. Uploads files via rsync
5. Creates and installs `homer2-web.service` (systemd)
6. Enables and starts the service
7. Cleans up local publish directory

## Managing the Service

```bash
# Status
ssh hanzzo@rpi-rover-brain4.local 'sudo systemctl status homer2-web.service'

# Live logs
ssh hanzzo@rpi-rover-brain4.local 'sudo journalctl -u homer2-web.service -f'

# Restart
ssh hanzzo@rpi-rover-brain4.local 'sudo systemctl restart homer2-web.service'

# Stop
ssh hanzzo@rpi-rover-brain4.local 'sudo systemctl stop homer2-web.service'
```

## API Access

After deployment:

```
http://rpi-rover-brain4.local:5144
```

Example – set LCD text:
```bash
curl -X POST http://rpi-rover-brain4.local:5144/api/lcd \
  -H "Content-Type: application/json" \
  -d '{"line1":"Hello","line2":"World!"}'
```

## Troubleshooting

### I2C / LCD not working
Ensure I2C is enabled:
```bash
ssh hanzzo@rpi-rover-brain4.local 'ls -la /dev/i2c*'
```

If empty, enable I2C:
```bash
ssh hanzzo@rpi-rover-brain4.local 'sudo raspi-config'
# Interface Options → I2C → Enable, then reboot
```

### Architecture mismatch
- `aarch64` → use `linux-arm64`
- `armv7l` → use `linux-arm`

### Updating
Run the deploy script again; it will stop, update files, and restart the service.
