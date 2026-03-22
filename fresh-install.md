# Fresh install notes

### 1. SSH keys (passwordless login from your Mac/Linux PC)

```bash
ssh-copy-id -i ~/.ssh/id_rsa hanzzo@rpi-rover-brain5.local
```


### 2. `raspi-config` (or Raspberry Pi Connect UI)

Run: `sudo raspi-config`

- **Interface Options → Serial Port**
  - **Login shell over serial:** **No** (must be off so nothing else uses the UART for a console).
  - **Serial port hardware:** **Yes** (enables the UART on the GPIO pins).

- **Interface Options → I2C** → **Yes** (LCD on the PCF8574).

- **Interface Options → Camera** → **Yes** if you use the Pi camera module.

Reboot when prompted (`sudo reboot`).

### 3. install `cloudflared` 

```
sudo mkdir -p --mode=0755 /usr/share/keyrings

curl -fsSL https://pkg.cloudflare.com/cloudflare-main.gpg | sudo tee /usr/share/keyrings/cloudflare-main.gpg >/dev/null

echo 'deb [signed-by=/usr/share/keyrings/cloudflare-main.gpg] https://pkg.cloudflare.com/cloudflared any main' | sudo tee /etc/apt/sources.list.d/cloudflared.list

sudo apt-get update && sudo apt-get install cloudflared
```

```
sudo cloudflared service install eyJhIjoiZDVhM2M0NDY0MWFhYzZiZGJiZTAxODdhNmY4Y2EzOTIiLCJ0IjoiYjJiNjExODUtOWY5My00Y2E3LTgxN2MtNWM0MzBmZTRlOThhIiwicyI6Ik1qRTFaakUxTm1NdE1HSTNZeTAwWTJNeExXRmxaakF0TmpNNFlqTXpObVEyTVdRMCJ9
```