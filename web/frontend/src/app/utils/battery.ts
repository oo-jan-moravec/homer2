/**
 * Maps pack voltage (V) to approximate charge % for an 11-cell NiMH stack
 * (~11 V empty … ~15.1 V full resting). Tune endpoints if your chemistry differs.
 */
export function batteryVoltageToPercent(v: number): number {
  const vEmpty = 11.0;
  const vFull = 15.1;
  if (v <= vEmpty) return 0;
  if (v >= vFull) return 100;
  return Math.round(((v - vEmpty) / (vFull - vEmpty)) * 100);
}
