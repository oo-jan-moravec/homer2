using System.Device.Gpio;

namespace RoverOperatorApi.Services;

/// <summary>
/// Blinks heartbeat LED (GPIO 26) in double-blink pattern while backend runs.
/// Visual confirmation that RPi is running. No-op when GPIO unavailable.
/// </summary>
public sealed class HeartbeatBackgroundService : BackgroundService
{
    private const int LedPin = 26;
    private readonly ILogger<HeartbeatBackgroundService> _logger;
    private GpioController? _ctrl;

    public HeartbeatBackgroundService(ILogger<HeartbeatBackgroundService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            _ctrl = new GpioController();
            _ctrl.OpenPin(LedPin, PinMode.Output);
            _logger.LogInformation("Heartbeat LED started on GPIO {Pin}", LedPin);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Heartbeat LED not available");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ctrl!.Write(LedPin, PinValue.High);
                await Task.Delay(100, stoppingToken);
                _ctrl.Write(LedPin, PinValue.Low);
                await Task.Delay(100, stoppingToken);
                _ctrl.Write(LedPin, PinValue.High);
                await Task.Delay(100, stoppingToken);
                _ctrl.Write(LedPin, PinValue.Low);
                await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }

        _ctrl?.Write(LedPin, PinValue.Low);
        _ctrl?.ClosePin(LedPin);
        _ctrl?.Dispose();
    }
}
