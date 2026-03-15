using System.Device.Gpio;

namespace RoverOperatorApi.Services;

public interface IIrService
{
    bool IsAvailable { get; }
    bool IsOn { get; }
    void Set(bool on);
    void Toggle();
}

/// <summary>
/// IR LED on GPIO 23. State persists in ~/.rover-ir.
/// No-op when not on RPi.
/// </summary>
public sealed class IrService : IIrService, IDisposable
{
    private const int IrPin = 23;
    private readonly ILogger<IrService> _logger;
    private GpioController? _ctrl;
    private bool _on;
    private bool _disposed;

    public bool IsAvailable => _ctrl != null;
    public bool IsOn => _on;

    public IrService(ILogger<IrService> logger)
    {
        _logger = logger;
        _on = LoadState();
        TryInit();
        if (_ctrl != null)
            _ctrl.Write(IrPin, _on ? PinValue.High : PinValue.Low);
    }

    private bool LoadState()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rover-ir");
            return File.Exists(path) && File.ReadAllText(path).Trim() == "1";
        }
        catch { return false; }
    }

    private void SaveState()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rover-ir");
            File.WriteAllText(path, _on ? "1" : "0");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save IR state");
        }
    }

    private void TryInit()
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            _ctrl = new GpioController();
            _ctrl.OpenPin(IrPin, PinMode.Output);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "IR GPIO not available");
        }
    }

    public void Set(bool on)
    {
        if (_ctrl == null) return;
        _on = on;
        _ctrl.Write(IrPin, on ? PinValue.High : PinValue.Low);
        SaveState();
    }

    public void Toggle()
    {
        Set(!_on);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ctrl?.Dispose();
    }
}
