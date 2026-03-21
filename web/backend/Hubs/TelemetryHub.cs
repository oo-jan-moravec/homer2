using Microsoft.AspNetCore.SignalR;
using RoverOperatorApi.Services;

namespace RoverOperatorApi.Hubs;

/// <summary>
/// Clients subscribe to receive telemetry. Server broadcasts when the serial ingress parses a telemetry line (e.g. v9 push every 5s).
/// </summary>
public class TelemetryHub : Hub
{
    private readonly ILatestTelemetryStore _latestTelemetry;

    public TelemetryHub(ILatestTelemetryStore latestTelemetry)
    {
        _latestTelemetry = latestTelemetry;
    }

    public override async Task OnConnectedAsync()
    {
        var snapshot = _latestTelemetry.Get();
        if (snapshot != null)
            await Clients.Caller.SendAsync("ReceiveTelemetry", snapshot);
        await base.OnConnectedAsync();
    }
}
