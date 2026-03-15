using Microsoft.AspNetCore.SignalR;
using RoverOperatorApi.Models;
using RoverOperatorApi.Services;

namespace RoverOperatorApi.Hubs;

/// <summary>
/// Client sends Drive(bearing, vel) while holding keys. Server forwards to rover.
/// Send 0,0 to stop. Watchdog 500ms - client should send every ~300ms.
/// </summary>
public class DriveHub : Hub
{
    private readonly IRoverSerialService _serial;

    public DriveHub(IRoverSerialService serial)
    {
        _serial = serial;
    }

    public Task Drive(DriveCommand cmd)
    {
        TelemetryBackgroundService.NotifyDriveActive();
        _serial.SendDrive(cmd.Bearing, cmd.Velocity);
        return Task.CompletedTask;
    }

    public Task Stop()
    {
        _serial.SendStop();
        return Task.CompletedTask;
    }
}
