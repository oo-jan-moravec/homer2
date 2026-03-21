namespace RoverOperatorApi.Models;

/// <summary>Runtime serial port stats and optional trace (see Rover:SerialTrace).</summary>
public sealed record SerialDebugSnapshot(
    long DriveSends,
    long DriveLockTimeouts,
    int TelemetryReadTimeoutMs,
    int DriveLockWaitMs,
    IReadOnlyList<SerialTraceLine> Recent,
    string? LastDriveLine);

public sealed record SerialTraceLine(string At, string Dir, string Line);
