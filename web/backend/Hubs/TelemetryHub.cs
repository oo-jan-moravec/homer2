using Microsoft.AspNetCore.SignalR;

namespace RoverOperatorApi.Hubs;

/// <summary>
/// Clients subscribe to receive telemetry. Server broadcasts from TelemetryBackgroundService.
/// </summary>
public class TelemetryHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }
}
