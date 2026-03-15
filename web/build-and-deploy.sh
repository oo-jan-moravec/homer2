#!/bin/bash
# Build Rover Operator Console: Angular frontend + .NET backend
# Angular bundle goes to backend/wwwroot, served as single app on RPi
#
# Usage:
#   ./build-and-deploy.sh          # build + deploy to RPi (systemd)
#   ./build-and-deploy.sh --build  # build only (no deploy)
#
# Env: RPI_HOST, RPI_USER
# Installs to /opt/rover-operator-console, requires sudo on RPi.

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_DIR="$SCRIPT_DIR/backend"
FRONTEND_DIR="$SCRIPT_DIR/frontend"
DEPLOY_DIR="$SCRIPT_DIR/deploy"
WWWROOT="$BACKEND_DIR/wwwroot"
TARGET="linux-arm64"
INSTALL_DIR="/opt/rover-operator-console"
BUILD_ONLY=false

[[ "${1:-}" == "--build" ]] && BUILD_ONLY=true

HOST="${RPI_HOST:-rpi-rover-brain4.local}"
RPI_USER="${RPI_USER:-hanzzo}"

echo "=== 1. Building Angular frontend ==="
cd "$FRONTEND_DIR"
npm run build

echo ""
echo "=== 2. Copying Angular bundle to backend/wwwroot ==="
ANGULAR_OUT="$FRONTEND_DIR/dist/frontend"
if [[ -d "$ANGULAR_OUT/browser" ]]; then
    ANGULAR_DIST="$ANGULAR_OUT/browser"
else
    ANGULAR_DIST="$ANGULAR_OUT"
fi

rm -rf "$WWWROOT"/*
cp -R "$ANGULAR_DIST"/* "$WWWROOT/"

echo ""
echo "=== 3. Building .NET backend for $TARGET ==="
cd "$BACKEND_DIR"
PUBLISH_OUT="$BACKEND_DIR/publish-out"
rm -rf "$PUBLISH_OUT"
dotnet publish -c Release -r "$TARGET" -o "$PUBLISH_OUT"

if $BUILD_ONLY; then
    echo ""
    echo "Build done. Output: $PUBLISH_OUT"
    echo "Run locally: dotnet run --project $BACKEND_DIR"
    exit 0
fi

SERVICE_FILE="$DEPLOY_DIR/rover-operator-console.service"
SVC_NAME="rover-operator-console"

echo ""
echo "=== 4. Deploying to ${RPI_USER}@${HOST} (rsync + systemd) ==="

# Rsync app files (only changed files); --delete removes obsolete files on remote
rsync -avz --delete --rsync-path="sudo rsync" \
  "$PUBLISH_OUT"/ "${RPI_USER}@${HOST}:${INSTALL_DIR}/"

# Service file: rsync to /tmp, substitute user, install
rsync -avz "$SERVICE_FILE" "${RPI_USER}@${HOST}:/tmp/rover-operator-console.service"

ssh "${RPI_USER}@${HOST}" bash -s "$RPI_USER" "$INSTALL_DIR" << 'REMOTE'
  set -e
  SVC_USER="$1"
  INSTALL_DIR="$2"
  SVC_NAME="rover-operator-console"

  sed "s/__SERVICE_USER__/$SVC_USER/g" /tmp/rover-operator-console.service | sudo tee /etc/systemd/system/${SVC_NAME}.service > /dev/null
  rm -f /tmp/rover-operator-console.service

  sudo chown -R "$SVC_USER:$SVC_USER" "$INSTALL_DIR"
  sudo chmod +x "$INSTALL_DIR/RoverOperatorApi"

  sudo systemctl daemon-reload
  sudo systemctl enable "$SVC_NAME"
  sudo systemctl restart "$SVC_NAME"

  echo ""
  echo "Status:"
  sudo systemctl status "$SVC_NAME" --no-pager
REMOTE

echo ""
echo "Done. Service: rover-operator-console"
echo "  sudo systemctl status rover-operator-console"
echo "  sudo systemctl restart rover-operator-console"
echo ""
echo "App: http://<rpi-ip>:5000"
