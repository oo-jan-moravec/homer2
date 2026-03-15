using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace RoverOperatorApi.Services;

public interface ISystemInfoService
{
    SystemInfoDto GetSystemInfo();
}

public record SystemInfoDto(
    string Hostname,
    string Os,
    string[] IpAddresses,
    string Uptime,
    string? CpuTempC,
    int CpuCores,
    string LoadAverage,
    string MemoryUsedMb,
    string MemoryTotalMb,
    int MemoryUsedPercent,
    string? DiskFreeGb,
    string? DiskTotalGb
);

public class SystemInfoService : ISystemInfoService
{
    private static readonly DateTime ProcessStart = DateTime.UtcNow;

    public SystemInfoDto GetSystemInfo()
    {
        var hostname = Environment.MachineName;
        var os = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
        var ipAddresses = GetIpAddresses();
        var uptime = GetUptime();
        var cpuTemp = GetCpuTemperature();
        var cpuCores = Environment.ProcessorCount;
        var loadAvg = GetLoadAverage();
        var (usedMb, totalMb) = GetMemoryInfo();
        var (freeGb, totalGb) = GetDiskInfo();
        var memPct = totalMb > 0 ? (int)Math.Round(100 * usedMb / totalMb) : 0;

        return new SystemInfoDto(
            Hostname: hostname,
            Os: os,
            IpAddresses: ipAddresses,
            Uptime: uptime,
            CpuTempC: cpuTemp,
            CpuCores: cpuCores,
            LoadAverage: loadAvg,
            MemoryUsedMb: usedMb.ToString("F1"),
            MemoryTotalMb: totalMb.ToString("F1"),
            MemoryUsedPercent: memPct,
            DiskFreeGb: freeGb.HasValue ? freeGb.Value.ToString("F2") : null,
            DiskTotalGb: totalGb.HasValue ? totalGb.Value.ToString("F2") : null
        );
    }

    private static string[] GetIpAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Distinct()
                .ToArray();
        }
        catch
        {
            return ["--"];
        }
    }

    private static string GetUptime()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                var lines = File.ReadAllLines("/proc/uptime");
                if (lines.Length > 0 && double.TryParse(lines[0].Split()[0], out var seconds))
                {
                    var ts = TimeSpan.FromSeconds(seconds);
                    return FormatUptime(ts);
                }
            }
            catch { }
        }

        var processUptime = DateTime.UtcNow - ProcessStart;
        return FormatUptime(processUptime) + " (proc)";
    }

    private static string FormatUptime(TimeSpan ts)
    {
        if (ts.TotalDays >= 1)
            return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Minutes}m {ts.Seconds}s";
    }

    private static string? GetCpuTemperature()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return null;

        try
        {
            var zones = Directory.GetDirectories("/sys/class/thermal").Where(d => d.Contains("thermal_zone")).OrderBy(d => d).ToArray();
            foreach (var zone in zones)
            {
                var tempPath = Path.Combine(zone, "temp");
                var typePath = Path.Combine(zone, "type");
                if (File.Exists(tempPath))
                {
                    var type = File.Exists(typePath) ? File.ReadAllText(typePath).Trim() : "unknown";
                    if (type.Contains("cpu", StringComparison.OrdinalIgnoreCase) || type == "x86_pkg_temp" || zones.Length == 1)
                    {
                        var raw = File.ReadAllText(tempPath).Trim();
                        if (int.TryParse(raw, out var millidegrees))
                            return (millidegrees / 1000.0).ToString("F1");
                    }
                }
            }
        }
        catch { }

        return null;
    }

    private static string GetLoadAverage()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                var line = File.ReadAllText("/proc/loadavg").Trim();
                var parts = line.Split();
                if (parts.Length >= 3)
                    return $"{parts[0]} {parts[1]} {parts[2]}";
            }
            catch { }
        }

        return "--";
    }

    private static (double usedMb, double totalMb) GetMemoryInfo()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var lines = File.ReadAllLines("/proc/meminfo");
                var memTotalKb = GetMemInfoValue(lines, "MemTotal");
                var memAvailableKb = GetMemInfoValue(lines, "MemAvailable");
                if (memTotalKb > 0)
                {
                    var usedKb = memTotalKb - memAvailableKb;
                    return (usedKb / 1024.0, memTotalKb / 1024.0);
                }
            }

            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var workingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0);
            return (workingSetMb, workingSetMb);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static long GetMemInfoValue(string[] lines, string key)
    {
        var line = lines.FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal));
        if (line == null) return 0;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var v) ? v : 0;
    }

    private static (double? freeGb, double? totalGb) GetDiskInfo()
    {
        try
        {
            var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady);
            if (drive?.IsReady == true)
                return (drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), drive.TotalSize / (1024.0 * 1024.0 * 1024.0));
        }
        catch { }

        return (null, null);
    }
}
