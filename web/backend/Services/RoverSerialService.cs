using System.IO.Ports;
using RoverOperatorApi.Models;

namespace RoverOperatorApi.Services;

/// <summary>
/// Owns the serial connection to the rover Arduino. Thread-safe.
/// Protocol: 115200 8N1, newline-terminated. T=telemetry, R=reset encoders,
/// "bearing vel"=drive. Watchdog 500ms.
/// </summary>
public interface IRoverSerialService
{
    bool IsConnected { get; }
    TelemetryData? RequestTelemetry(CancellationToken ct = default);
    void SendDrive(int bearing, int velocity);
    void SendStop();
    void ResetEncoders(CancellationToken ct = default);
    void SendEncoderConfig(bool enabled, int? kp = null, int? max = null, CancellationToken ct = default);
}

public sealed class RoverSerialService : IRoverSerialService, IDisposable
{
    private readonly string _portName;
    private readonly ILogger<RoverSerialService> _logger;
    private SerialPort? _port;
    private readonly SemaphoreSlim _serialLock = new(1, 1);
    private bool _disposed;
    private bool _connectionAttempted;

    public bool IsConnected => _port?.IsOpen ?? false;

    public RoverSerialService(IConfiguration config, ILogger<RoverSerialService> logger)
    {
        _portName = config["Rover:SerialPort"] ?? "/dev/serial0";
        _logger = logger;
    }

    public void EnsureConnected()
    {
        if (_port?.IsOpen == true) return;
        if (_connectionAttempted && _port == null) return;

        _connectionAttempted = true;
        try
        {
            _port?.Dispose();
            _port = new SerialPort(_portName, 115200) { ReadTimeout = 2000, WriteTimeout = 1000 };
            _port.Open();
            _logger.LogInformation("Serial connected: {Port}", _portName);
        }
        catch (Exception ex)
        {
            _port = null;
            _logger.LogWarning(ex, "Serial not available: {Port} (running without rover hardware)", _portName);
        }
    }

    public TelemetryData? RequestTelemetry(CancellationToken ct = default)
    {
        EnsureConnected();
        if (_port?.IsOpen != true) return null;
        if (!_serialLock.Wait(Timeout.Infinite, ct)) return null;

        try
        {
            _port!.DiscardInBuffer();
            _port.WriteLine("T");
            var line = _port.ReadLine()?.Trim();
            return ParseTelemetry(line);
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("Telemetry timeout");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telemetry error");
            return null;
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
        if (!_serialLock.Wait(100)) return;

        try
        {
            _port!.WriteLine($"{Math.Clamp(bearing, 0, 359)} {Math.Clamp(velocity, 0, 9)}");
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
            _port?.WriteLine("R");
            _ = _port?.ReadLine();
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
            _port?.WriteLine(cmd);
            _ = _port?.ReadLine();
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
            !int.TryParse(parts[4], out var vR) || !double.TryParse(parts[5], out var vBat))
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
