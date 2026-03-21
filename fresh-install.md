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

### 2. Serial device group

So the rover app (and `rover-test`) can open the port without root:

```bash
sudo usermod -a -G dialout $USER
```

Log out and SSH back in (or reboot) so the group applies.

### 3. Check UART before blaming the web app

```bash
ls -l /dev/serial0
groups   # should include dialout
```

Optional quick loopback test (with Arduino disconnected, only if you know what you’re doing): many people instead run **`rover-test telemetry`** or **`rover-test drive`** once the Arduino is wired.

### 4. Pi models with Bluetooth (e.g. Zero 2 W, 3, 4, 5)

Bluetooth can steal the **good** UART. If `/dev/serial0` exists but traffic to the Arduino is garbage or absent, add **one** of these to **`/boot/firmware/config.txt`** (Bookworm) or **`/boot/config.txt`** (older images), then reboot:

- **`dtoverlay=disable-bt`** — turns Bluetooth off; main UART goes to GPIO 14/15 (simplest if you don’t need BT on the Pi).
- Or use the overlay that maps Bluetooth to the mini-UART (model-specific; see Raspberry Pi UART docs).

### 5. Config path on Bookworm

Firmware config is often **`/boot/firmware/config.txt`**. If a setting “does nothing”, confirm you edited the file that actually gets read on boot.

