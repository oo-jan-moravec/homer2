#!/usr/bin/env bash
# Call the homer2-web LCD endpoint to set text on the I2C display
# Usage: ./set-lcd.sh [line1] [line2]
#   ./set-lcd.sh "ahoj kajo"       -> line1="ahoj kajo", line2 blank
#   ./set-lcd.sh "ahoj" "kajo"    -> line1="ahoj", line2="kajo"
# Env: LCD_HOST (default rpi-rover-brain4.local), LCD_PORT (default 5144)

set -e

HOST="${LCD_HOST:-rpi-rover-brain4.local}"
PORT="${LCD_PORT:-5144}"
URL="http://${HOST}:${PORT}/api/lcd"

LINE1="${1:-}"
LINE2="${2:-}"

# Treat "-" as empty string
[[ "$LINE1" == "-" ]] && LINE1=""
[[ "$LINE2" == "-" ]] && LINE2=""

# Escape for JSON: backslash and double-quote
json_esc() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }

BODY="{\"line1\":\"$(json_esc "$LINE1")\",\"line2\":\"$(json_esc "$LINE2")\"}"
curl -s -w "\n" -X POST "$URL" -H "Content-Type: application/json" -d "$BODY"
