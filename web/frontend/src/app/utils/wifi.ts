/** Friendly WiFi signal label from RSSI dBm. */
export function wifiRssiToLabel(rssi: number | null | undefined): string | null {
  if (rssi == null) return null;
  if (rssi >= -50) return 'Excellent';
  if (rssi >= -60) return 'Good';
  if (rssi >= -70) return 'Fair';
  if (rssi >= -80) return 'Weak';
  if (rssi >= -90) return 'Poor';
  return 'Very poor';
}

/** For technical display: friendly label plus raw dBm. */
export function wifiRssiToLabelAndDb(rssi: number | null | undefined): string {
  if (rssi == null) return '--';
  const label = wifiRssiToLabel(rssi)!;
  return `${label} (${rssi} dBm)`;
}
