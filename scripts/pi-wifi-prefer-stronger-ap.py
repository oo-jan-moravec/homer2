#!/usr/bin/env python3
"""
Optional helper for Raspberry Pi: if wpa_supplicant is in use (typical wpa_cli socket
under /run/wpa_supplicant), periodically scan and roam to a stronger AP on the *same*
SSID (e.g. mesh with one SSID, multiple BSSIDs).

Install on the Pi (example):
  sudo install -m755 pi-wifi-prefer-stronger-ap.py /usr/local/bin/
  sudo crontab -e   # */5 * * * * /usr/local/bin/pi-wifi-prefer-stronger-ap.py

Env:
  WIFI_IFACE      default wlan0
  MIN_DB_BETTER   minimum dBm improvement to roam (default 10, reduces flapping)
"""
from __future__ import annotations

import os
import re
import subprocess
import sys
import time

_BSSID_ROW = re.compile(r"^[0-9a-fA-F]{2}(:[0-9a-fA-F]{2}){5}\s*\t")


def _run(argv: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(argv, check=False, capture_output=True, text=True)


def _iw_signal_dbm(iface: str) -> float | None:
    r = _run(["iw", "dev", iface, "link"])
    if r.returncode != 0:
        return None
    for line in r.stdout.splitlines():
        if "signal:" not in line:
            continue
        parts = line.split()
        for i, p in enumerate(parts):
            if p == "signal:" and i + 1 < len(parts):
                try:
                    return float(parts[i + 1])
                except ValueError:
                    return None
    return None


def main() -> int:
    iface = os.environ.get("WIFI_IFACE", "wlan0")
    try:
        min_delta = float(os.environ.get("MIN_DB_BETTER", "10"))
    except ValueError:
        min_delta = 10.0

    st = _run(["wpa_cli", "-i", iface, "status"])
    if st.returncode != 0:
        return 0

    status: dict[str, str] = {}
    for line in st.stdout.splitlines():
        if "=" in line:
            k, v = line.split("=", 1)
            status[k.strip()] = v.strip()

    ssid = status.get("ssid")
    cur_bssid = (status.get("bssid") or "").lower()
    if not ssid or not cur_bssid or status.get("wpa_state") != "COMPLETED":
        return 0

    cur_sig = _iw_signal_dbm(iface)
    if cur_sig is None:
        return 0

    _run(["wpa_cli", "-i", iface, "scan"])
    time.sleep(4)

    sr = _run(["wpa_cli", "-i", iface, "scan_results"])
    if sr.returncode != 0:
        return 0

    best_bssid: str | None = None
    best_sig = -999.0
    for line in sr.stdout.splitlines():
        line = line.rstrip("\n")
        if not line.strip() or not _BSSID_ROW.match(line):
            continue
        fields = line.split("\t", 4)
        if len(fields) < 5:
            continue
        bssid, _freq, level_s, _flags, row_ssid = fields
        bssid = bssid.strip().lower()
        row_ssid = row_ssid.strip()
        if row_ssid != ssid or bssid == cur_bssid:
            continue
        try:
            sig = float(level_s.strip())
        except ValueError:
            continue
        if sig > best_sig:
            best_sig = sig
            best_bssid = bssid

    if best_bssid is None or best_sig < cur_sig + min_delta:
        return 0

    print(
        f"wifi roam: {cur_sig:.0f} dBm -> {best_sig:.0f} dBm (bssid {best_bssid})",
        file=sys.stderr,
    )
    ro = _run(["wpa_cli", "-i", iface, "roam", best_bssid])
    return 0 if ro.returncode == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
