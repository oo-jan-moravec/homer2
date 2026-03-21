# Fresh install notes

## SSH keys (passwordless login from your Mac/Linux PC)

```bash
ssh-copy-id -i ~/.ssh/id_rsa hanzzo@rpi-rover-brain5.local
```

## Raspberry Pi OS (after reinstall) — interfaces for Homer2

The rover backend expects **`/dev/serial0`** at **115200** to the Arduino (GPIO 14 TX / 15 RX), **I2C** for the LCD, and your login user able to open the serial port.

### 1. `raspi-config` (or Raspberry Pi Connect UI)

Run: `sudo raspi-config`

- **Interface Options → Serial Port**
  - **Login shell over serial:** **No** (must be off so nothing else uses the UART for a console).
  - **Serial port hardware:** **Yes** (enables the UART on the GPIO pins).

- **Interface Options → I2C** → **Yes** (LCD on the PCF8574).

- **Interface Options → Camera** → **Yes** if you use the Pi camera module.

Reboot when prompted (`sudo reboot`).
