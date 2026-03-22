using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RoverOperatorApi.Services;

public interface ISystemPowerService
{
    /// <summary>True when remote shutdown API should be advertised and allowed (Linux + config).</summary>
    bool IsRemoteShutdownEnabled { get; }

    /// <summary>Start <c>sudo shutdown -h now</c>. Returns false if not allowed or start failed.</summary>
    bool TryScheduleHalt(out string? errorDetail);
}

public sealed class SystemPowerService : ISystemPowerService
{
    private readonly ILogger<SystemPowerService> _logger;
    private readonly bool _enabled;

    public SystemPowerService(IConfiguration config, ILogger<SystemPowerService> logger)
    {
        _logger = logger;
        _enabled = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                   && config.GetValue("Rover:RemoteShutdownEnabled", false);
    }

    public bool IsRemoteShutdownEnabled => _enabled;

    public bool TryScheduleHalt(out string? errorDetail)
    {
        errorDetail = null;
        if (!_enabled)
        {
            errorDetail = "Remote shutdown is disabled or not supported on this host.";
            return false;
        }

        try
        {
            // Requires sudoers, e.g. service_user ALL=(root) NOPASSWD: /sbin/shutdown
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/sudo",
                Arguments = "-n /sbin/shutdown -h now",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                errorDetail = "Could not start shutdown process.";
                return false;
            }

            _logger.LogInformation("Scheduled system halt (shutdown -h now)");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to schedule halt");
            errorDetail = ex.Message;
            return false;
        }
    }
}
