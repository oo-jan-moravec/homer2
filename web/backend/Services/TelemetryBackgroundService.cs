using Microsoft.AspNetCore.SignalR;
using RoverOperatorApi.Hubs;

namespace RoverOperatorApi.Services;

/// <summary>
/// Consumes newline-terminated lines from the rover serial port (e.g. v9 push every 5s).
/// Parses CSV telemetry, augments with host metrics, updates the store, and broadcasts via SignalR.
/// </summary>
public sealed class TelemetryBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<TelemetryBackgroundService> _logger;

    public TelemetryBackgroundService(IServiceProvider services, ILogger<TelemetryBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1500, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var serial = scope.ServiceProvider.GetRequiredService<IRoverSerialService>();
                var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<TelemetryHub>>();
                var store = scope.ServiceProvider.GetRequiredService<ILatestTelemetryStore>();

                if (serial.TryReadTelemetryLine(out var telem) && telem != null)
                {
                    var wifiRssi = SystemInfoService.GetWifiRssiDb();
                    var pingMs = SystemInfoService.GetPingMs();
                    var augmented = telem with { WifiRssiDb = wifiRssi, PingMs = pingMs };
                    store.Set(augmented);
                    await hubContext.Clients.All.SendAsync("ReceiveTelemetry", augmented, stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Telemetry ingress");
            }

            await Task.Delay(5, stoppingToken);
        }
    }
}
