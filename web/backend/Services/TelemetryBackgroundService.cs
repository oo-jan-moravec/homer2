using Microsoft.AspNetCore.SignalR;
using RoverOperatorApi.Hubs;

namespace RoverOperatorApi.Services;

/// <summary>
/// Periodically sends T command to rover, parses telemetry, broadcasts via SignalR.
/// Skips polling when drive is active (last drive &lt; 600ms ago) to avoid blocking.
/// </summary>
public sealed class TelemetryBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<TelemetryBackgroundService> _logger;
    private const int PollIntervalMs = 250;
    private const int DriveCooldownMs = 600;
    private static long _lastDriveTicks;

    public static void NotifyDriveActive()
    {
        Interlocked.Exchange(ref _lastDriveTicks, Environment.TickCount64);
    }

    public TelemetryBackgroundService(IServiceProvider services, ILogger<TelemetryBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(2000, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Environment.TickCount64 - Interlocked.Read(ref _lastDriveTicks) < DriveCooldownMs)
                {
                    await Task.Delay(PollIntervalMs, stoppingToken);
                    continue;
                }

                using var scope = _services.CreateScope();
                var serial = scope.ServiceProvider.GetRequiredService<IRoverSerialService>();
                var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<TelemetryHub>>();

                var telem = serial.RequestTelemetry(stoppingToken);
                if (telem != null)
                {
                    var store = scope.ServiceProvider.GetRequiredService<ILatestTelemetryStore>();
                    store.Set(telem);
                    await hubContext.Clients.All.SendAsync("ReceiveTelemetry", telem, stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Telemetry poll");
            }

            await Task.Delay(PollIntervalMs, stoppingToken);
        }
    }
}
