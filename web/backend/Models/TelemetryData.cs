namespace RoverOperatorApi.Models;

/// <summary>
/// Telemetry from rover Arduino (CSV line: on-demand T and/or periodic push) plus host-augmented fields.
/// Reply format: le,re,dist_mm,vL,vR,vBat[,us_mm]. us_mm = HC-SR04 range (mm); omitted or negative = no reading.
/// WifiRssiDb from host /proc/net/wireless. PingMs: RTT to 8.8.8.8 in ms.
/// </summary>
public record TelemetryData(
    long LeftEdges,
    long RightEdges,
    long DistanceMm,
    int VelocityLeftMmps,
    int VelocityRightMmps,
    double BatteryVoltage,
    int? UltrasonicMm = null,
    int? WifiRssiDb = null,
    int? PingMs = null
);
