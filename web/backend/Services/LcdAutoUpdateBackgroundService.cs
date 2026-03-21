namespace RoverOperatorApi.Services;

/// <summary>
/// On startup and periodically: line1 = CPU temp + WiFi bars, line2 = BAT bars MEM%.
/// Uses LCD custom chars: \0=empty block, \x01=full block.
/// Format: "CPU 45°C WF████" / "BAT█████ MEM56%"
/// </summary>
public sealed class LcdAutoUpdateBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<LcdAutoUpdateBackgroundService> _logger;
    private const int UpdateIntervalMs = 2000;

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

                var telem = telemetryStore.Get();
                if (!didInitial || autoUpdate.Enabled)
                {
                    var line1 = FormatLine1(telem, sysInfo);
                    var line2 = FormatLine2(telem, sysInfo);
                    lcd.Write(line1, line2);
                    if (!didInitial)
                    {
                        _logger.LogInformation("LCD initial: {Line1} / {Line2}", line1, line2);
                        didInitial = true;
                    }
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

    private const char BarEmpty = '\0';  // LCD custom char 0
    private const char BarFull = '\x01'; // LCD custom char 1
    private const char Degree = '\x02';  // LCD custom char 2 (degree symbol)

    /// <summary>Line 1: CPU XX°C WF plus 4 block bars (empty/full).</summary>
    private static string FormatLine1(Models.TelemetryData? telem, ISystemInfoService sysInfo)
    {
        var dto = sysInfo.GetSystemInfo();
        var tempStr = !string.IsNullOrEmpty(dto.CpuTempC) && double.TryParse(dto.CpuTempC, out var t)
            ? $"{(int)Math.Round(t)}"
            : "--";
        var wifiBars = WifiRssiToBars(telem?.WifiRssiDb);
        var wifiStr = "WF" + BarChars(wifiBars, 4);
        return $"CPU {tempStr}{Degree}C {wifiStr}";
    }

    /// <summary>Line 2: BAT plus 5 block bars, MEMxx%.</summary>
    private static string FormatLine2(Models.TelemetryData? telem, ISystemInfoService sysInfo)
    {
        var dto = sysInfo.GetSystemInfo();
        var batBars = BatteryToBars(telem);
        var batStr = "BAT" + BarChars(batBars, 5);
        var memStr = $"MEM{dto.MemoryUsedPercent}%";
        return $"{batStr} {memStr}";
    }

    /// <summary>Returns filled full blocks + remaining empty blocks. Unknown (-1) = all empty.</summary>
    private static string BarChars(int filled, int total)
    {
        var f = filled < 0 ? 0 : Math.Min(filled, total);
        return new string(BarFull, f) + new string(BarEmpty, total - f);
    }

    /// <summary>0-5 bars from battery voltage. -1 if unknown.</summary>
    private static int BatteryToBars(Models.TelemetryData? telem)
    {
        if (telem == null) return -1;
        var pct = BatteryVoltageToPercent(telem.BatteryVoltage);
        if (pct <= 0) return 0;
        return Math.Min(5, (int)Math.Ceiling(pct / 20.0));
    }

    /// <summary>0-4 bars from RSSI dBm. -1 if unknown.</summary>
    private static int WifiRssiToBars(int? rssi)
    {
        if (rssi == null) return -1;
        if (rssi >= -50) return 4;
        if (rssi >= -60) return 3;
        if (rssi >= -70) return 2;
        if (rssi >= -80) return 1;
        return 0;
    }

    /// <summary>11-cell NiMH: ~11 V empty, ~15.1 V full (resting). Matches frontend battery.ts.</summary>
    private static int BatteryVoltageToPercent(double v)
    {
        const double vEmpty = 11.0;
        const double vFull = 15.1;
        if (v <= vEmpty) return 0;
        if (v >= vFull) return 100;
        return (int)Math.Round((v - vEmpty) / (vFull - vEmpty) * 100.0);
    }
}
