namespace RoverOperatorApi.Models;

/// <summary>
/// Telemetry from rover Arduino (T command) plus host-augmented fields.
/// Reply format: le,re,dist_mm,vL,vR,vBat. WifiRssiDb from host /proc/net/wireless.
/// PingMs: RTT to 8.8.8.8 in ms.
/// </summary>
public record TelemetryData(
    long LeftEdges,
    long RightEdges,
    long DistanceMm,
    int VelocityLeftMmps,
    int VelocityRightMmps,
    double BatteryVoltage,
    int? WifiRssiDb = null,
    int? PingMs = null
);
