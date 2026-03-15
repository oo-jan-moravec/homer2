using System.Device.I2c;

namespace homer2_web.Services;

public interface ILcdService
{
    Task<LcdResult> SetTextAsync(string? line1, string? line2, CancellationToken ct = default);
}

public record LcdResult(bool Success, string? Error);

/// <summary>
/// Raw HD44780 over PCF8574 I2C. Bypasses Iot.Device.Bindings which had timing issues.
/// PCF8574 pins: P0=RS, P1=RW, P2=E, P3=BL, P4-P7=D4-D7
/// </summary>
public class LcdService : ILcdService
{
    private readonly IConfiguration _config;
    private const int MaxLineLength = 16;

    public LcdService(IConfiguration config) => _config = config;

    public Task<LcdResult> SetTextAsync(string? line1, string? line2, CancellationToken ct = default)
    {
        line1 ??= string.Empty;
        line2 ??= string.Empty;

        if (line1.Length > MaxLineLength) line1 = line1[..MaxLineLength];
        if (line2.Length > MaxLineLength) line2 = line2[..MaxLineLength];

        try
        {
            var busId = _config.GetValue("Lcd:I2cBusId", 1);
            var addr = _config.GetValue("Lcd:I2cDeviceAddress", "0x27");
            using var i2c = I2cDevice.Create(new I2cConnectionSettings(busId, Convert.ToInt32(addr, 16)));
            var lcd = new Pcf8574Lcd(i2c);

            lcd.Init();
            Thread.Sleep(2);

            foreach (var c in line1)
            {
                lcd.WriteData((byte)c);
                Thread.Sleep(1);
            }

            if (line2.Length > 0)
            {
                lcd.SetDdramAddress(0x40); // Line 2 start
                Thread.Sleep(1);
                foreach (var c in line2)
                {
                    lcd.WriteData((byte)c);
                    Thread.Sleep(1);
                }
            }

            return Task.FromResult(new LcdResult(true, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new LcdResult(false, ex.Message));
        }
    }
}

/// <summary>
/// HD44780 4-bit mode over PCF8574 I2C backpack. Direct protocol implementation.
/// </summary>
internal sealed class Pcf8574Lcd : IDisposable
{
    private readonly I2cDevice _i2c;
    private const byte BL = 0x08;  // Backlight
    private const byte E = 0x04;   // Enable
    private const byte RW = 0x02;  // Read/Write (0=write)
    private const byte RS = 0x01;  // Register Select (1=data, 0=command)

    public Pcf8574Lcd(I2cDevice i2c) => _i2c = i2c;

    public void Dispose() => _i2c.Dispose();

    private void WriteNibble(byte nibble, bool isData)
    {
        byte b = (byte)((nibble << 4) | BL);
        if (isData) b |= RS;

        _i2c.WriteByte(b);          // E low
        _i2c.WriteByte((byte)(b | E));  // E high
        _i2c.WriteByte(b);          // E low
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

    public void Clear()
    {
        WriteCommand(0x01);
        Thread.Sleep(2);
    }

    public void SetDdramAddress(byte addr) => WriteCommand((byte)(0x80 | addr));

    public void Init()
    {
        Thread.Sleep(50);
        WriteNibble(0x03, false);
        Thread.Sleep(5);
        WriteNibble(0x03, false);
        Thread.Sleep(1);
        WriteNibble(0x03, false);
        WriteNibble(0x02, false); // 4-bit mode
        WriteCommand(0x28);       // 4-bit, 2 lines, 5x8
        WriteCommand(0x0C);      // Display on, cursor off
        WriteCommand(0x06);       // Entry mode: increment
        Clear();
    }
}
