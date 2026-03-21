using RoverOperatorApi.Models;

namespace RoverOperatorApi.Services;

/// <summary>
/// Thread-safe store for the most recent telemetry from the rover.
/// Updated when a telemetry CSV line arrives from the rover serial ingress, read by LcdAutoUpdateBackgroundService.
/// </summary>
public interface ILatestTelemetryStore
{
    void Set(TelemetryData data);
    TelemetryData? Get();
}

public sealed class LatestTelemetryStore : ILatestTelemetryStore
{
    private TelemetryData? _latest;
    private readonly object _lock = new();

    public void Set(TelemetryData data)
    {
        lock (_lock)
        {
            _latest = data;
        }
    }

    public TelemetryData? Get()
    {
        lock (_lock)
        {
            return _latest;
        }
    }
}
