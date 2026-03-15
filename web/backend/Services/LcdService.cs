using System.Device.I2c;

namespace RoverOperatorApi.Services;

public interface ILcdService
{
    bool IsAvailable { get; }
    void Write(string line1, string line2);
    void Clear();
}

/// <summary>
/// I2C PCF8574 LCD @ 0x27 (same as rover-test).
/// No-op when not on RPi or I2C unavailable.
/// </summary>
public sealed class LcdService : ILcdService, IDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<LcdService> _logger;
    private Pcf8574Lcd? _lcd;
    private bool _disposed;

    public bool IsAvailable => _lcd != null;

    public LcdService(IConfiguration config, ILogger<LcdService> logger)
    {
        _config = config;
        _logger = logger;
        TryInit();
    }

    private void TryInit()
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            var busId = _config.GetValue("Rover:Lcd:BusId", 1);
            var address = _config.GetValue("Rover:Lcd:Address", 0x27);
            var i2c = I2cDevice.Create(new I2cConnectionSettings(busId, address));
            _lcd = new Pcf8574Lcd(i2c);
            _lcd.Init();
            Thread.Sleep(100);
            _logger.LogInformation("LCD initialized");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LCD not available");
        }
    }

    public void Write(string line1, string line2)
    {
        if (_lcd == null) return;
        try
        {
            _lcd.Clear();
            Thread.Sleep(2);
            foreach (var c in Truncate(line1, 16)) { _lcd.WriteData((byte)c); Thread.Sleep(1); }
            if (!string.IsNullOrEmpty(line2))
            {
                _lcd.SetDdramAddress(0x40);
                Thread.Sleep(1);
                foreach (var c in Truncate(line2, 16)) { _lcd.WriteData((byte)c); Thread.Sleep(1); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LCD write failed");
        }
    }

    public void Clear()
    {
        _lcd?.Clear();
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lcd?.Dispose();
    }

    private sealed class Pcf8574Lcd : IDisposable
    {
        private readonly I2cDevice _i2c;
        private const byte BL = 0x08, E = 0x04, RS = 0x01;

        public Pcf8574Lcd(I2cDevice i2c) => _i2c = i2c;

        private void WriteNibble(byte nibble, bool isData)
        {
            byte b = (byte)((nibble << 4) | BL);
            if (isData) b |= RS;
            _i2c.WriteByte(b);
            _i2c.WriteByte((byte)(b | E));
            _i2c.WriteByte(b);
            Thread.Sleep(1);
        }

        private void WriteCommand(byte cmd)
        {
            WriteNibble((byte)(cmd >> 4), false);
            WriteNibble((byte)(cmd & 0x0F), false);
        }

        public void WriteData(byte data)
        {
            WriteNibble((byte)(data >> 4), true);
            WriteNibble((byte)(data & 0x0F), true);
        }

        public void Clear() { WriteCommand(0x01); Thread.Sleep(2); }
        public void SetDdramAddress(byte addr) => WriteCommand((byte)(0x80 | addr));

        public void Init()
        {
            Thread.Sleep(50);
            WriteNibble(0x03, false); Thread.Sleep(5);
            WriteNibble(0x03, false); Thread.Sleep(1);
            WriteNibble(0x03, false);
            WriteNibble(0x02, false);
            WriteCommand(0x28);
            WriteCommand(0x0C);
            WriteCommand(0x06);
            Clear();
        }

        public void Dispose() => _i2c.Dispose();
    }
}
