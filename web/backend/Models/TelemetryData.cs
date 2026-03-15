namespace RoverOperatorApi.Models;

/// <summary>
/// Telemetry from rover Arduino (T command).
/// Reply format: le,re,dist_mm,vL,vR,vBat
/// </summary>
public record TelemetryData(
    long LeftEdges,
    long RightEdges,
    long DistanceMm,
    int VelocityLeftMmps,
    int VelocityRightMmps,
    double BatteryVoltage
);
