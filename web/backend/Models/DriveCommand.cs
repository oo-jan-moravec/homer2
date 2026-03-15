namespace RoverOperatorApi.Models;

/// <summary>
/// Drive command: bearing 0-359 (0=fwd, 90=rt, 180=back, 270=left), velocity 0-9.
/// Watchdog: send every ~300ms or motors stop.
/// </summary>
public record DriveCommand(int Bearing, int Velocity);
