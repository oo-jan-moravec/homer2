namespace RoverOperatorApi.Services;

/// <summary>
/// On startup: sets LCD line1=IP, line2=voltage/mem%/temp.
/// Periodically updates line2 when auto-update is enabled.
/// Format: "11.95V M56% 41C"
/// </summary>
public sealed class LcdAutoUpdateBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<LcdAutoUpdateBackgroundService> _logger;
    private const int UpdateIntervalMs = 2000;
    private string? _line1Ip;

    public LcdAutoUpdateBackgroundService(IServiceProvider services, ILogger<LcdAutoUpdateBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(3000, stoppingToken);
        var didInitial = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var lcd = scope.ServiceProvider.GetRequiredService<ILcdService>();
                var sysInfo = scope.ServiceProvider.GetRequiredService<ISystemInfoService>();
                var telemetryStore = scope.ServiceProvider.GetRequiredService<ILatestTelemetryStore>();
                var autoUpdate = scope.ServiceProvider.GetRequiredService<ILcdAutoUpdateService>();

                if (!lcd.IsAvailable)
                {
                    await Task.Delay(UpdateIntervalMs, stoppingToken);
                    continue;
                }

                if (!didInitial)
                {
                    _line1Ip = GetPrimaryIp(sysInfo);
                    var line2 = FormatLine2(telemetryStore.Get(), sysInfo);
                    lcd.Write(_line1Ip ?? "--", line2);
                    didInitial = true;
                    _logger.LogInformation("LCD initial: {Line1} / {Line2}", _line1Ip, line2);
                }
                else if (autoUpdate.Enabled)
                {
                    var line2 = FormatLine2(telemetryStore.Get(), sysInfo);
                    lcd.Write(_line1Ip ?? "--", line2);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LCD auto-update");
            }

            await Task.Delay(UpdateIntervalMs, stoppingToken);
        }
    }

    private static string? GetPrimaryIp(ISystemInfoService sysInfo)
    {
        var dto = sysInfo.GetSystemInfo();
        return dto.IpAddresses.Length > 0 ? dto.IpAddresses[0] : null;
    }

    private static string FormatLine2(Models.TelemetryData? telem, ISystemInfoService sysInfo)
    {
        var dto = sysInfo.GetSystemInfo();
        var voltage = telem != null ? $"{telem.BatteryVoltage:F2}V" : "--V";
        var mem = $"M{dto.MemoryUsedPercent}%";
        var temp = !string.IsNullOrEmpty(dto.CpuTempC) && double.TryParse(dto.CpuTempC, out var t)
            ? $"{(int)Math.Round(t)}C"
            : "--C";
        return $"{voltage} {mem} {temp}";
    }
}
