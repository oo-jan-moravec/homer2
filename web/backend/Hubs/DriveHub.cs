using Microsoft.AspNetCore.SignalR;
using RoverOperatorApi.Services;

namespace RoverOperatorApi.Hubs;

/// <summary>
/// Client sends Drive(bearing, vel) while holding keys. Server forwards to rover.
/// Send 0,0 to stop. Watchdog 500ms - client should send every ~300ms.
/// Uses primitive parameters so SignalR binds reliably (object/record payloads can fail to deserialize).
/// </summary>
public class DriveHub : Hub
{
    private readonly IRoverSerialService _serial;

    public DriveHub(IRoverSerialService serial)
    {
        _serial = serial;
    }

    public Task Drive(int bearing, int velocity)
    {
        _serial.SendDrive(bearing, velocity);
        return Task.CompletedTask;
    }

    public Task Stop()
    {
        _serial.SendStop();
        return Task.CompletedTask;
    }
}
