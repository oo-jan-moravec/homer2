#!/bin/bash
# Build rover-test for Raspberry Pi (linux-arm64) and deploy via SCP
#
# Usage:
#   ./build-and-deploy.sh          # build + deploy
#   ./build-and-deploy.sh --build  # build only (no deploy)
#
# Env: RPI_HOST, RPI_USER, RPI_REMOTE_DIR

set -e

HOST="${RPI_HOST:-rpi-rover-brain4.local}"
USER="${RPI_USER:-hanzzo}"
REMOTE_DIR="${RPI_REMOTE_DIR:-~/test-scripts/rover-test}"
TARGET="linux-arm64"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/rover-test"
OUTPUT_DIR="$PROJECT_DIR/publish"
BUILD_ONLY=false

[[ "${1:-}" == "--build" ]] && BUILD_ONLY=true

echo "=== Building rover-test for $TARGET (self-contained single-file) ==="
cd "$PROJECT_DIR"
dotnet publish -c Release -r "$TARGET" -o "$OUTPUT_DIR"

BINARY="$OUTPUT_DIR/rover-test"
if [[ ! -x "$BINARY" ]]; then
    echo "Error: Binary not found at $BINARY"
    exit 1
fi

if $BUILD_ONLY; then
    echo "Build done. Binary: $BINARY"
    exit 0
fi

echo ""
echo "=== Deploying to ${USER}@${HOST}:${REMOTE_DIR} ==="
ssh "${USER}@${HOST}" "mkdir -p ${REMOTE_DIR}"
scp "$BINARY" "${USER}@${HOST}:${REMOTE_DIR}/"
ssh "${USER}@${HOST}" "chmod +x ${REMOTE_DIR}/rover-test"

echo ""
echo "Done. Run on RPi (no dotnet required):"
echo "  ${REMOTE_DIR}/rover-test [command]"
echo ""
echo "Commands: lcd | heartbeat | telemetry | ir | ultrasound | camera | sound"
