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
/// Mitigates I2C noise/power issues: retries with backoff, longer delays, periodic reinit.
/// For hardware: add dtparam=i2c_arm_baudrate=10000 to /boot/config.txt to slow bus.
/// </summary>
public sealed class LcdService : ILcdService, IDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<LcdService> _logger;
    private Pcf8574Lcd? _lcd;
    private bool _disposed;
    private readonly object _lcdLock = new();
    private int _writeCount;
    private const int MaxRetries = 3;
    private static readonly int[] RetryDelaysMs = { 50, 100, 200 };

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
        lock (_lcdLock)
        {
            for (var attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        Thread.Sleep(RetryDelaysMs[attempt - 1]);
                        _lcd.Init();
                        Thread.Sleep(10);
                    }
                    _writeCount++;
                    var fullReinit = _writeCount % 30 == 0;
                    if (fullReinit)
                    {
                        _lcd.Init();
                        Thread.Sleep(10);
                    }
                    else
                    {
                        _lcd.ReloadCustomChars();
                        _lcd.Clear();
                        Thread.Sleep(5);
                    }
                    _lcd.SetDdramAddress(0);
                    Thread.Sleep(2);
                    foreach (var c in Truncate(line1, 16)) { _lcd.WriteData((byte)c); Thread.Sleep(2); }
                    if (!string.IsNullOrEmpty(line2))
                    {
                        _lcd.SetDdramAddress(0x40);
                        Thread.Sleep(2);
                        foreach (var c in Truncate(line2, 16)) { _lcd.WriteData((byte)c); Thread.Sleep(2); }
                    }
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "LCD write failed (attempt {Attempt}/{Max})", attempt + 1, MaxRetries);
                }
            }
        }
    }

    public void Clear()
    {
        if (_lcd == null) return;
        lock (_lcdLock)
        {
            try { _lcd.Clear(); } catch (Exception ex) { _logger.LogWarning(ex, "LCD clear failed"); }
        }
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
            Thread.Sleep(2); // Extra settling for noisy I2C / power
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

        public void Clear() { WriteCommand(0x01); Thread.Sleep(5); }
        public void SetDdramAddress(byte addr) => WriteCommand((byte)(0x80 | addr));

        /// <summary>Reload custom chars into CGRAM. Call before each write to recover from corruption.</summary>
        public void ReloadCustomChars()
        {
            InitCustomChars();
        }

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
            InitCustomChars();
            Clear();
        }

        /// <summary>Load custom chars into CGRAM: 0=empty block, 1=full block, 2=degree symbol.</summary>
        private void InitCustomChars()
        {
            // Char 0: empty block (hollow rectangle)
            WriteCommand(0x40); // CGRAM addr 0
            foreach (var b in new byte[] { 0x1F, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1F })
            { WriteData(b); Thread.Sleep(2); }
            // Char 1: full block (solid)
            WriteCommand(0x48); // CGRAM addr 8 (char 1)
            foreach (var b in new byte[] { 0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x1F })
            { WriteData(b); Thread.Sleep(2); }
            // Char 2: degree symbol (°) - small circle
            WriteCommand(0x50); // CGRAM addr 16 (char 2)
            foreach (var b in new byte[] { 0x0E, 0x11, 0x11, 0x0E, 0x00, 0x00, 0x00, 0x00 })
            { WriteData(b); Thread.Sleep(2); }
        }

        public void Dispose() => _i2c.Dispose();
    }
}
