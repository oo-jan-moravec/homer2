namespace RoverOperatorApi.Services;

/// <summary>
/// On startup: sets LCD line1=IP, line2=battery%/mem%/temp.
/// Periodically updates line2 when auto-update is enabled.
/// Format: "BAT75% MEM56% 41C"
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
        var batPct = telem != null ? $"BAT{BatteryVoltageToPercent(telem.BatteryVoltage)}%" : "BAT--%";
        var mem = $"MEM{dto.MemoryUsedPercent}%";
        var temp = !string.IsNullOrEmpty(dto.CpuTempC) && double.TryParse(dto.CpuTempC, out var t)
            ? $"{(int)Math.Round(t)}"
            : "--";
        return $"{batPct} {mem} {temp}";
    }

    private static int BatteryVoltageToPercent(double v)
    {
        if (v >= 12.6) return 100;
        if (v >= 12.2) return 90;
        if (v >= 11.7) return 75;
        if (v >= 11.3) return 60;
        if (v >= 10.8) return 50;
        if (v >= 10.4) return 35;
        if (v >= 9.9) return 20;
        if (v >= 9.45) return 10;
        return 0;
    }
}
