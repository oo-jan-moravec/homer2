using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Ports;
using RoverOperatorApi.Models;

namespace RoverOperatorApi.Services;

/// <summary>
/// Owns the serial connection to the rover Arduino. Thread-safe.
/// Protocol: 115200 8N1, newline-terminated. T=telemetry, R=reset encoders,
/// "bearing vel"=drive. Watchdog 500ms. Telemetry: ingest unprompted CSV lines (v9) via TryReadTelemetryLine.
/// </summary>
public interface IRoverSerialService
{
    bool IsConnected { get; }
    /// <summary>Latest telemetry from the store (updated by serial ingress when the rover sends CSV).</summary>
    TelemetryData? RequestTelemetry(CancellationToken ct = default);
    /// <summary>Non-blocking read: if the lock is free, reads up to one line and parses telemetry CSV.</summary>
    bool TryReadTelemetryLine(out TelemetryData? data);
    void SendDrive(int bearing, int velocity);
    void SendStop();
    void ResetEncoders(CancellationToken ct = default);
    void SendEncoderConfig(bool enabled, int? kp = null, int? max = null, CancellationToken ct = default);
    SerialDebugSnapshot GetSerialDebug();
}

public sealed class RoverSerialService : IRoverSerialService, IDisposable
{
    /// <summary>How long RequestTelemetry may block on ReadLine (keep below drive lock wait).</summary>
    private const int DefaultTelemetryReadTimeoutMs = 600;

    /// <summary>Max wait for the serial lock when sending drive (telemetry holds lock for ReadLine).</summary>
    private const int DefaultDriveLockWaitMs = 5000;

    private readonly string _portName;
    private readonly int _telemetryReadTimeoutMs;
    private readonly int _driveLockWaitMs;
    private readonly bool _serialTrace;
    private readonly ILatestTelemetryStore _latestTelemetry;
    private readonly ILogger<RoverSerialService> _logger;
    private SerialPort? _port;
    private readonly SemaphoreSlim _serialLock = new(1, 1);
    private bool _disposed;
    private DateTimeOffset _nextSerialOpenAttemptUtc = DateTimeOffset.MinValue;
    private long _driveSends;
    private long _driveLockTimeouts;
    private string? _lastDriveLine;
    private readonly ConcurrentQueue<SerialTraceLine> _trace = new();

    public bool IsConnected => _port?.IsOpen ?? false;

    public RoverSerialService(IConfiguration config, ILatestTelemetryStore latestTelemetry, ILogger<RoverSerialService> logger)
    {
        _latestTelemetry = latestTelemetry;
        _portName = config["Rover:SerialPort"] ?? "/dev/serial0";
        _telemetryReadTimeoutMs = int.TryParse(config["Rover:TelemetryReadTimeoutMs"], out var tr) ? Math.Clamp(tr, 100, 5000) : DefaultTelemetryReadTimeoutMs;
        _driveLockWaitMs = int.TryParse(config["Rover:DriveLockWaitMs"], out var dw) ? Math.Clamp(dw, 500, 30_000) : DefaultDriveLockWaitMs;
        _serialTrace = string.Equals(config["Rover:SerialTrace"], "true", StringComparison.OrdinalIgnoreCase);
        _logger = logger;
    }

    public SerialDebugSnapshot GetSerialDebug()
    {
        var recent = _serialTrace ? _trace.ToArray() : Array.Empty<SerialTraceLine>();
        return new SerialDebugSnapshot(
            Interlocked.Read(ref _driveSends),
            Interlocked.Read(ref _driveLockTimeouts),
            _telemetryReadTimeoutMs,
            _driveLockWaitMs,
            recent,
            _lastDriveLine);
    }

    private void Trace(string dir, string line)
    {
        if (!_serialTrace) return;
        var entry = new SerialTraceLine(DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff"), dir, line);
        _trace.Enqueue(entry);
        while (_trace.Count > 128 && _trace.TryDequeue(out _)) { }
    }

    public void EnsureConnected()
    {
        if (_port?.IsOpen == true) return;

        var now = DateTimeOffset.UtcNow;
        if (now < _nextSerialOpenAttemptUtc) return;

        try
        {
            _port?.Dispose();
            _port = new SerialPort(_portName, 115200)
            {
                ReadTimeout = _telemetryReadTimeoutMs,
                WriteTimeout = 1000,
                NewLine = "\n"
            };
            _port.Open();
            _nextSerialOpenAttemptUtc = DateTimeOffset.MinValue;
            _logger.LogInformation("Serial connected: {Port}", _portName);
        }
        catch (Exception ex)
        {
            _port = null;
            _nextSerialOpenAttemptUtc = now.AddSeconds(3);
            _logger.LogWarning(ex, "Serial not available: {Port} (will retry)", _portName);
        }
    }

    public TelemetryData? RequestTelemetry(CancellationToken ct = default)
    {
        _ = ct;
        return _latestTelemetry.Get();
    }

    public bool TryReadTelemetryLine(out TelemetryData? data)
    {
        data = null;
        EnsureConnected();
        if (_port?.IsOpen != true) return false;
        if (!_serialLock.Wait(0)) return false;

        try
        {
            _port.ReadTimeout = 50;
            string? line;
            try
            {
                line = _port.ReadLine()?.Trim();
            }
            catch (TimeoutException)
            {
                return false;
            }

            if (string.IsNullOrEmpty(line)) return false;
            Trace("rx", line);
            data = ParseTelemetry(line);
            return data != null;
        }
        finally
        {
            _serialLock.Release();
        }
    }

    public void SendDrive(int bearing, int velocity)
    {
        EnsureConnected();
        if (_port?.IsOpen != true) return;
        if (!_serialLock.Wait(_driveLockWaitMs))
        {
            Interlocked.Increment(ref _driveLockTimeouts);
            _logger.LogWarning("Drive skipped: serial lock not acquired within {Ms}ms (telemetry or encoder command holding port)", _driveLockWaitMs);
            return;
        }

        try
        {
            var line = $"{Math.Clamp(bearing, 0, 359)} {Math.Clamp(velocity, 0, 9)}";
            Trace("tx", line);
            _port!.WriteLine(line);
            _lastDriveLine = line;
            Interlocked.Increment(ref _driveSends);
        }
        finally
        {
            _serialLock.Release();
        }
    }

    public void SendStop()
    {
        SendDrive(0, 0);
    }

    public void ResetEncoders(CancellationToken ct = default)
    {
        EnsureConnected();
        if (_port?.IsOpen != true) return;

        _serialLock.Wait(ct);
        try
        {
            _port?.DiscardInBuffer();
            Trace("tx", "R");
            _port?.WriteLine("R");
            var rLine = _port?.ReadLine();
            Trace("rx", rLine?.Trim() ?? "");
        }
        catch { /* ignore */ }
        finally
        {
            _serialLock.Release();
        }
    }

    public void SendEncoderConfig(bool enabled, int? kp = null, int? max = null, CancellationToken ct = default)
    {
        EnsureConnected();
        if (_port?.IsOpen != true) return;

        var cmd = enabled ? $"ENC 1{((kp.HasValue && kp > 0) ? $" {kp}" : "")}{((max.HasValue && max > 0) ? $" {max}" : "")}" : "ENC 0";
        _serialLock.Wait(ct);
        try
        {
            _port?.DiscardInBuffer();
            Trace("tx", cmd);
            _port?.WriteLine(cmd);
            var eLine = _port?.ReadLine();
            Trace("rx", eLine?.Trim() ?? "");
        }
        catch { /* ignore */ }
        finally
        {
            _serialLock.Release();
        }
    }

    private static TelemetryData? ParseTelemetry(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var parts = line.Split(',');
        if (parts.Length < 6) return null;
        if (!long.TryParse(parts[0], out var le) || !long.TryParse(parts[1], out var re) ||
            !long.TryParse(parts[2], out var dist) || !int.TryParse(parts[3], out var vL) ||
            !int.TryParse(parts[4], out var vR) ||
            !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var vBat))
            return null;
        return new TelemetryData(le, re, dist, vL, vR, vBat, null);
    }

    public void Dispose() => Dispose(true);
    private void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        _port?.Dispose();
        _port = null;
        _serialLock.Dispose();
    }
}
