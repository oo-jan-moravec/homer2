using System.Diagnostics;
using System.Device.Gpio;
using System.Device.I2c;
using System.IO.Ports;

if (args.Length == 0)
    return RunInteractiveMenu();

var cmd = args[0].ToLowerInvariant();
return cmd switch
{
    "lcd" => RunLcd(CancellationToken.None),
    "heartbeat" => RunHeartbeat(CancellationToken.None),
    "telemetry" => RunTelemetry(CancellationToken.None),
    "drive" => RunDrive(CancellationToken.None),
    "ir" => RunIr(CancellationToken.None),
    "ultrasound" => RunUltrasound(CancellationToken.None),
    "camera" => RunCamera(),
    "sound" => RunSound(),
    _ => PrintUsage()
};

static int RunInteractiveMenu()
{
    var menu = new[] { "lcd", "heartbeat", "telemetry", "drive", "ir", "ultrasound", "camera", "sound" };
    while (true)
    {
        Console.WriteLine();
        Console.WriteLine("rover-test - Rover hardware test suite");
        Console.WriteLine("--------------------------------------");
        for (var i = 0; i < menu.Length; i++)
            Console.WriteLine($"  {i + 1}. {menu[i]}");
        Console.WriteLine("  0. exit");
        Console.Write("ENTER TEST NUMBER> ");
        var input = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(input) || input == "0" || input == "q" || input == "exit") return 0;

        var idx = int.TryParse(input, out var n) && n >= 1 && n <= menu.Length ? n - 1 : -1;
        if (idx < 0)
            idx = Array.FindIndex(menu, m => m.StartsWith(input, StringComparison.OrdinalIgnoreCase));

        if (idx >= 0)
        {
            Console.WriteLine($"\nRunning: {menu[idx]} (Ctrl+C to stop)");
            var r = RunCommand(menu[idx]);
            if (r != 0) Console.WriteLine("(test failed)");
        }
        else
            Console.WriteLine("Unknown option.");
    }
}

static int RunCommand(string cmd) => cmd switch
{
    "lcd" => RunLcd(CancellationToken.None),
    "heartbeat" => RunHeartbeat(CancellationToken.None),
    "telemetry" => RunTelemetry(CancellationToken.None),
    "drive" => RunDrive(CancellationToken.None),
    "ir" => RunIr(CancellationToken.None),
    "ultrasound" => RunUltrasound(CancellationToken.None),
    "camera" => RunCamera(),
    "sound" => RunSound(),
    _ => 0
};

static int PrintUsage()
{
    Console.WriteLine("""
        rover-test - Rover hardware test suite

        Usage: rover-test [command]
              rover-test          Interactive menu (no args)

        Commands:
          lcd         LCD display: cycles text (I2C PCF8574 @ 0x27)
          heartbeat   LED double-blink on GPIO 26
          telemetry   Request one telemetry line from Arduino
          drive       Interactive drive (WASD, 0-9 speed, space=stop, q=quit)
          ir          IR LED toggle: run turns on (stays on), run again turns off, etc.
          ultrasound  HC-SR04 distance on GPIO 24/25
          camera      Capture image via rpicam-still / libcamera-still
          sound       Record 2s from USB mic, play back (plughw:1,0)
        """);
    return 1;
}

// --- LCD (I2C PCF8574) ---
static int RunLcd(CancellationToken ct)
{
    const int BusId = 1, Address = 0x27;
    var texts = new[] { ("Hello Honza", "RPI LCD works!"), ("Line 1 text", "Line 2 text"), ("Cycling", "through texts...") };

    var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    try
    {
        using var i2c = I2cDevice.Create(new I2cConnectionSettings(BusId, Address));
        var lcd = new Pcf8574Lcd(i2c);
        lcd.Init();
        Thread.Sleep(100);

        while (!cts.Token.IsCancellationRequested)
        {
            foreach (var (line1, line2) in texts)
            {
                if (cts.Token.IsCancellationRequested) break;
                lcd.Clear();
                Thread.Sleep(2);
                foreach (var c in Truncate(line1, 16)) { lcd.WriteData((byte)c); Thread.Sleep(1); }
                if (!string.IsNullOrEmpty(line2))
                {
                    lcd.SetDdramAddress(0x40);
                    Thread.Sleep(1);
                    foreach (var c in Truncate(line2, 16)) { lcd.WriteData((byte)c); Thread.Sleep(1); }
                }
                Thread.Sleep(3000);
            }
        }
        lcd.Clear();
    }
    catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }
    return 0;
}

// --- Heartbeat LED (GPIO 26) ---
static int RunHeartbeat(CancellationToken ct)
{
    const int LedPin = 26;
    var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    try
    {
        using var ctrl = new GpioController();
        ctrl.OpenPin(LedPin, PinMode.Output);

        while (!cts.Token.IsCancellationRequested)
        {
            ctrl.Write(LedPin, PinValue.High); Thread.Sleep(100);
            ctrl.Write(LedPin, PinValue.Low); Thread.Sleep(100);
            ctrl.Write(LedPin, PinValue.High); Thread.Sleep(100);
            ctrl.Write(LedPin, PinValue.Low);
            Thread.Sleep(1000);
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }
    return 0;
}

// --- Serial telemetry (/dev/serial0) ---
static int RunTelemetry(CancellationToken ct)
{
    try
    {
        using var port = new SerialPort("/dev/serial0", 115200) { ReadTimeout = 1000 };
        port.Open();
        port.WriteLine("T");
        var line = port.ReadLine();
        Console.WriteLine(string.IsNullOrWhiteSpace(line) ? "(no reply)" : line);
    }
    catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }
    return 0;
}

// --- Drive (Arduino serial: bearing 0-359, vel 0-9, watchdog 500ms) ---
static int RunDrive(CancellationToken ct)
{
    var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    try
    {
        using var port = new SerialPort("/dev/serial0", 115200) { ReadTimeout = 100 };
        port.Open();

        Console.WriteLine("""
            Drive test - /dev/serial0 @ 115200
            W=forward S=back A=left D=right | 0-9=velocity | Space=stop Q=quit
            (Watchdog: send every 300ms)
            """);

        var bearing = 0;   // 0=fwd, 90=rt, 180=bk, 270=lt
        var vel = 5;
        var lastSend = Stopwatch.GetTimestamp();

        while (!cts.Token.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                switch (key.Key)
                {
                    case ConsoleKey.W: bearing = 0; break;
                    case ConsoleKey.S: bearing = 180; break;
                    case ConsoleKey.A: bearing = 270; break;
                    case ConsoleKey.D: bearing = 90; break;
                    case ConsoleKey.Spacebar: vel = 0; break;
                    case ConsoleKey.Q: vel = 0; port.WriteLine("0 0"); return 0;
                    case ConsoleKey.D0: vel = 0; break;
                    case ConsoleKey.D1: case ConsoleKey.NumPad1: vel = 1; break;
                    case ConsoleKey.D2: case ConsoleKey.NumPad2: vel = 2; break;
                    case ConsoleKey.D3: case ConsoleKey.NumPad3: vel = 3; break;
                    case ConsoleKey.D4: case ConsoleKey.NumPad4: vel = 4; break;
                    case ConsoleKey.D5: case ConsoleKey.NumPad5: vel = 5; break;
                    case ConsoleKey.D6: case ConsoleKey.NumPad6: vel = 6; break;
                    case ConsoleKey.D7: case ConsoleKey.NumPad7: vel = 7; break;
                    case ConsoleKey.D8: case ConsoleKey.NumPad8: vel = 8; break;
                    case ConsoleKey.D9: case ConsoleKey.NumPad9: vel = 9; break;
                }
            }

            var now = Stopwatch.GetTimestamp();
            if ((now - lastSend) * 1000.0 / Stopwatch.Frequency >= 300)
            {
                port.WriteLine($"{bearing} {vel}");
                lastSend = now;
            }
            Thread.Sleep(50);
        }

        port.WriteLine("0 0");
    }
    catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }
    return 0;
}

// --- IR LED (GPIO 23) toggle (persists between runs) ---
static int RunIr(CancellationToken ct)
{
    const int IrPin = 23;
    var statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rover-test-ir");
    var wasOn = File.Exists(statePath) && File.ReadAllText(statePath).Trim() == "1";

    try
    {
        using var ctrl = new GpioController();
        ctrl.OpenPin(IrPin, PinMode.Output);
        var on = !wasOn;
        ctrl.Write(IrPin, on ? PinValue.High : PinValue.Low);
        File.WriteAllText(statePath, on ? "1" : "0");
        Console.WriteLine(on ? "IR ON" : "IR OFF");
    }
    catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }
    return 0;
}

// --- Ultrasound HC-SR04 (TRIG=25, ECHO=24) ---
static int RunUltrasound(CancellationToken ct)
{
    const int TrigPin = 25, EchoPin = 24;
    var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    try
    {
        using var ctrl = new GpioController();
        ctrl.OpenPin(TrigPin, PinMode.Output);
        ctrl.OpenPin(EchoPin, PinMode.Input);
        ctrl.Write(TrigPin, PinValue.Low);
        Thread.Sleep(500);

        while (!cts.Token.IsCancellationRequested)
        {
            var dist = MeasureDistance(ctrl, TrigPin, EchoPin);
            Console.WriteLine(dist is { } d ? $"Distance: {d:F1} cm" : "No echo / timeout");
            Thread.Sleep(500);
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }
    return 0;
}

static double? MeasureDistance(GpioController ctrl, int trig, int echo)
{
    ctrl.Write(trig, PinValue.Low);
    Thread.Sleep(1);
    ctrl.Write(trig, PinValue.High);
    var t0 = Stopwatch.GetTimestamp();
    while ((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency < 12) { }
    ctrl.Write(trig, PinValue.Low);

    var deadline = Stopwatch.GetTimestamp() + (long)(0.03 * Stopwatch.Frequency);
    while (ctrl.Read(echo) == PinValue.Low && Stopwatch.GetTimestamp() < deadline) { }
    var pulseStart = Stopwatch.GetTimestamp();
    if (ctrl.Read(echo) == PinValue.Low) return null;

    while (ctrl.Read(echo) == PinValue.High && Stopwatch.GetTimestamp() < deadline) { }
    var pulseEnd = Stopwatch.GetTimestamp();
    var pulseUs = (pulseEnd - pulseStart) * 1e6 / Stopwatch.Frequency;
    if (pulseUs > 30000) return null;
    return pulseUs * 0.0343 / 2; // ~343 m/s -> cm, /2 round-trip
}

// --- Camera (rpicam-still / libcamera-still) ---
static int RunCamera()
{
    var outPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "camera-test.jpg");
    var candidates = new[] { "rpicam-still", "libcamera-still" };
    var foundExe = false;
    foreach (var exe in candidates)
    {
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"-o \"{outPath}\" -t 1",
                    UseShellExecute = false,
                    RedirectStandardError = true
                }
            };
            proc.Start();
            foundExe = true;
            var err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);
            if (proc.ExitCode == 0)
            {
                Console.WriteLine($"OK - Captured: {outPath}");
                return 0;
            }
            Console.Error.WriteLine($"{exe} failed: {err}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            continue;
        }
    }
    if (!foundExe && OperatingSystem.IsLinux())
        Console.Error.WriteLine("No camera app found. Install: sudo apt install -y libcamera-apps");
    return 1;
}

// --- Sound (USB mic + speakers plughw:1,0) ---
static int RunSound()
{
    if (!OperatingSystem.IsLinux())
    {
        Console.Error.WriteLine("Sound test runs on Linux (RPi) only.");
        return 1;
    }

    var tmpFile = Path.GetTempFileName();
    const string dev = "plughw:1,0";
    const int sampleRate = 16000;

    try
    {
        Console.WriteLine("Recording 2 seconds from mic... (speak into the mic)");
        using (var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "arecord",
                Arguments = $"-D {dev} -d 2 -t raw -f S16_LE -r {sampleRate} -c 1 \"{tmpFile}\"",
                UseShellExecute = false,
                RedirectStandardError = true
            }
        })
        {
            proc.Start();
            var err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0)
            {
                Console.Error.WriteLine($"arecord failed: {err}");
                return 1;
            }
        }

        var size = new FileInfo(tmpFile).Length;
        if (size < 1000)
        {
            Console.Error.WriteLine($"Recording too small ({size} bytes) - mic may not be working");
            return 1;
        }
        Console.WriteLine($"Recorded {size} bytes (~{size / (sampleRate * 2)} seconds)");

        Console.WriteLine("Playing back... (check speakers)");
        using (var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "aplay",
                Arguments = $"-D {dev} -t raw -f S16_LE -r {sampleRate} -c 1 \"{tmpFile}\"",
                UseShellExecute = false,
                RedirectStandardError = true
            }
        })
        {
            proc.Start();
            var err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0)
            {
                Console.Error.WriteLine($"aplay failed: {err}");
                return 1;
            }
        }

        Console.WriteLine("OK - Mic and speakers working");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
    finally
    {
        try { File.Delete(tmpFile); } catch { }
    }
}

static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

// --- PCF8574 LCD driver ---
internal sealed class Pcf8574Lcd : IDisposable
{
    private readonly I2cDevice _i2c;
    private const byte BL = 0x08, E = 0x04, RW = 0x02, RS = 0x01;

    public Pcf8574Lcd(I2cDevice i2c) => _i2c = i2c;
    public void Dispose() => _i2c.Dispose();

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
}
