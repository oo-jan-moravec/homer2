/** Maps battery voltage (V) to approximate charge percentage for Li-ion/LiPo. */
export function batteryVoltageToPercent(v: number): number {
  if (v >= 12.6) return 100;
  if (v >= 12.2) return 90;
  if (v >= 11.7) return 75;
  if (v >= 11.3) return 60;
  if (v >= 10.8) return 50;
  if (v >= 10.4) return 35;
  if (v >= 9.9) return 20;
  if (v >= 9.45) return 10;
  return 0;
}
