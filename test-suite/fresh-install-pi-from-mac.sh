#!/usr/bin/env bash
# Run on your Mac. Optional steps per fresh-install.md: ssh-copy-id, cloudflared.
# Do raspi-config on the Pi yourself; this script prints reminders at the end.
set -euo pipefail

DEFAULT_SSH_USER="hanzzo"
# Override with env CLOUDFLARED_TOKEN=... or edit default below (same token as fresh-install.md).
DEFAULT_CLOUDFLARED_TOKEN='eyJhIjoiZDVhM2M0NDY0MWFhYzZiZGJiZTAxODdhNmY4Y2EzOTIiLCJ0IjoiYjJiNjExODUtOWY5My00Y2E3LTgxN2MtNWM0MzBmZTRlOThhIiwicyI6Ik1qRTFaakUxTm1NdE1HSTNZeTAwWTJNeExXRmxaakF0TmpNNFlqTXpObVEyTVdRMCJ9'

die() {
  echo "error: $*" >&2
  exit 1
}

confirm() {
  local prompt="$1"
  local ans
  read -rp "${prompt} [y/N] " ans
  [[ "${ans}" =~ ^[Yy]$ ]]
}

pick_ssh_identity() {
  # ssh-copy-id -i expects the private key path (see fresh-install.md).
  if [[ -f "${HOME}/.ssh/id_ed25519" ]]; then
    echo "${HOME}/.ssh/id_ed25519"
  elif [[ -f "${HOME}/.ssh/id_rsa" ]]; then
    echo "${HOME}/.ssh/id_rsa"
  else
    die "no ~/.ssh/id_ed25519 or ~/.ssh/id_rsa — create a key first (ssh-keygen)"
  fi
}

read -rp "Raspberry Pi hostname (e.g. rpi-rover-brain6.local): " PI_HOST
[[ -n "${PI_HOST// }" ]] || die "hostname is required"

read -rp "SSH user on Pi [${DEFAULT_SSH_USER}]: " SSH_USER
SSH_USER=${SSH_USER:-$DEFAULT_SSH_USER}

TARGET="${SSH_USER}@${PI_HOST}"

if confirm "Run ssh-copy-id (passwordless SSH; may ask for Pi password once)?"; then
  IDENTITY="$(pick_ssh_identity)"
  echo "using identity: ${IDENTITY}"
  echo ""
  echo "=== ssh-copy-id → ${TARGET} ==="
  ssh-copy-id -i "${IDENTITY}" "${TARGET}"
else
  echo "Skipped ssh-copy-id."
fi

if confirm "Install cloudflared (apt repo + package + tunnel service) on the Pi?"; then
  CLOUDFLARED_TOKEN="${CLOUDFLARED_TOKEN:-$DEFAULT_CLOUDFLARED_TOKEN}"
  [[ -n "${CLOUDFLARED_TOKEN}" ]] || die "set CLOUDFLARED_TOKEN or DEFAULT_CLOUDFLARED_TOKEN in this script"
  echo ""
  echo "=== cloudflared on ${TARGET} ==="
  ssh -o StrictHostKeyChecking=accept-new "${TARGET}" bash -s -- "${CLOUDFLARED_TOKEN}" <<'REMOTE_CF'
set -euo pipefail
TOKEN="$1"
sudo mkdir -p --mode=0755 /usr/share/keyrings
curl -fsSL https://pkg.cloudflare.com/cloudflare-main.gpg | sudo tee /usr/share/keyrings/cloudflare-main.gpg >/dev/null
echo 'deb [signed-by=/usr/share/keyrings/cloudflare-main.gpg] https://pkg.cloudflare.com/cloudflared any main' | sudo tee /etc/apt/sources.list.d/cloudflared.list
sudo apt-get update
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y cloudflared
sudo cloudflared service install "${TOKEN}"
REMOTE_CF
  echo "cloudflared install finished. Check: ssh ${TARGET} 'sudo systemctl status cloudflared'"
else
  echo "Skipped cloudflared."
fi

echo ""
echo "=== Do on the Pi: raspi-config (not automated) ==="
echo "  ssh ${TARGET}"
echo "  sudo raspi-config"
echo ""
echo "  Interface Options → Serial Port"
echo "    - Login shell over serial: No"
echo "    - Serial port hardware: Yes"
echo ""
echo "  Interface Options → I2C → Yes  (LCD on PCF8574)"
echo ""
echo "  Interface Options → Camera → Yes  (only if you use the Pi camera module)"
echo ""
echo "Then reboot when prompted: sudo reboot"
