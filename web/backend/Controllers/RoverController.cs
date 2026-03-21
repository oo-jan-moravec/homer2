using Microsoft.AspNetCore.Mvc;
using RoverOperatorApi.Models;
using RoverOperatorApi.Services;

namespace RoverOperatorApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RoverController : ControllerBase
{
    private readonly IRoverSerialService _serial;
    private readonly ILcdService _lcd;
    private readonly IIrService _ir;
    private readonly ICameraService _camera;
    private readonly ICameraStreamService _cameraStream;
    private readonly IVideoQualityService _videoQuality;
    private readonly IAudioStreamService _audioStream;
    private readonly ISystemInfoService _systemInfo;
    private readonly ILcdAutoUpdateService _lcdAutoUpdate;

    public RoverController(
        IRoverSerialService serial,
        ILcdService lcd,
        IIrService ir,
        ICameraService camera,
        ICameraStreamService cameraStream,
        IVideoQualityService videoQuality,
        IAudioStreamService audioStream,
        ISystemInfoService systemInfo,
        ILcdAutoUpdateService lcdAutoUpdate)
    {
        _serial = serial;
        _lcd = lcd;
        _ir = ir;
        _camera = camera;
        _cameraStream = cameraStream;
        _videoQuality = videoQuality;
        _audioStream = audioStream;
        _systemInfo = systemInfo;
        _lcdAutoUpdate = lcdAutoUpdate;
    }

    /// <summary>Host system info: IP, uptime, CPU temp, memory, etc.</summary>
    [HttpGet("sysinfo")]
    public IActionResult GetSystemInfo()
    {
        return Ok(_systemInfo.GetSystemInfo());
    }

    /// <summary>Overall status: serial connected, hardware availability.</summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            serialConnected = _serial.IsConnected,
            lcdAvailable = _lcd.IsAvailable,
            irAvailable = _ir.IsAvailable,
            irOn = _ir.IsOn,
            cameraAvailable = _camera.IsAvailable,
            soundAvailable = _audioStream.IsAvailable,
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Drive send counts, lock timeouts, and optional TX/RX trace. Enable <c>Rover:SerialTrace</c> for Recent lines.
    /// Use while reproducing flaky joystick/console drive (watch driveLockTimeouts).
    /// </summary>
    [HttpGet("serial-debug")]
    public IActionResult GetSerialDebug()
    {
        return Ok(_serial.GetSerialDebug());
    }

    /// <summary>
    /// Same write path as SignalR <c>Drive</c>: sends <c>bearing velocity</c> on the configured serial port.
    /// Use with curl when the test-suite drives but the UI does not — if this also does nothing, the problem is not SignalR (check <c>serialConnected</c>, port, service user).
    /// </summary>
    [HttpPost("drive")]
    public IActionResult PostDrive([FromBody] DriveRequest req)
    {
        _serial.SendDrive(req.Bearing, req.Velocity);
        return Ok(new { ok = true, serialConnected = _serial.IsConnected });
    }

    /// <summary>Request one telemetry snapshot (REST fallback; use SignalR for streaming).</summary>
    [HttpGet("telemetry")]
    public IActionResult GetTelemetry(CancellationToken ct)
    {
        var telem = _serial.RequestTelemetry(ct);
        return telem != null ? Ok(telem) : StatusCode(503, "Serial unavailable or timeout");
    }

    /// <summary>Set LCD lines (max 16 chars each).</summary>
    [HttpPost("lcd")]
    public IActionResult SetLcd([FromBody] LcdRequest req)
    {
        _lcd.Write(req.Line1 ?? "", req.Line2 ?? "");
        return Ok();
    }

    /// <summary>Clear LCD.</summary>
    [HttpPost("lcd/clear")]
    public IActionResult ClearLcd()
    {
        _lcd.Clear();
        return Ok();
    }

    /// <summary>Get LCD auto-update (periodic line2 refresh) enabled state.</summary>
    [HttpGet("lcd/auto")]
    public IActionResult GetLcdAutoEnabled()
    {
        return Ok(new { enabled = _lcdAutoUpdate.Enabled });
    }

    /// <summary>Set LCD auto-update enabled. When disabled, manual LCD writes work without being overwritten.</summary>
    [HttpPost("lcd/auto")]
    public IActionResult SetLcdAutoEnabled([FromBody] LcdAutoRequest req)
    {
        _lcdAutoUpdate.Enabled = req.Enabled;
        return Ok(new { enabled = _lcdAutoUpdate.Enabled });
    }

    /// <summary>Set IR LED on/off.</summary>
    [HttpPost("ir")]
    public IActionResult SetIr([FromBody] IrRequest req)
    {
        _ir.Set(req.On);
        return Ok(new { on = _ir.IsOn });
    }

    /// <summary>Toggle IR LED.</summary>
    [HttpPost("ir/toggle")]
    public IActionResult ToggleIr()
    {
        _ir.Toggle();
        return Ok(new { on = _ir.IsOn });
    }

    /// <summary>Get video stream quality preset (1080p, 720p, 480p, 240p).</summary>
    [HttpGet("camera/quality")]
    public IActionResult GetCameraQuality()
    {
        return Ok(new { preset = _videoQuality.Preset });
    }

    /// <summary>Set video stream quality preset. Lower res = less memory/CPU. Takes effect when stream restarts.</summary>
    [HttpPost("camera/quality")]
    public IActionResult SetCameraQuality([FromBody] CameraQualityRequest req)
    {
        _videoQuality.SetPreset(req.Preset ?? "480p");
        return Ok(new { preset = _videoQuality.Preset });
    }

    /// <summary>Capture camera image. Returns JPEG.</summary>
    [HttpGet("camera")]
    public async Task<IActionResult> CaptureCamera(CancellationToken ct)
    {
        var bytes = await _camera.CaptureAsync(ct);
        if (bytes == null || bytes.Length == 0)
            return StatusCode(503, "Camera capture failed or not available");
        return File(bytes, "image/jpeg");
    }

    /// <summary>Live MJPEG stream. Use as img src for real-time video feed.</summary>
    [HttpGet("camera/stream")]
    public async Task StreamCamera(CancellationToken ct)
    {
        if (!_cameraStream.IsAvailable)
        {
            Response.StatusCode = 503;
            await Response.WriteAsync("Camera stream not available", ct);
            return;
        }

        Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";

        var body = Response.Body;
        await foreach (var frame in _cameraStream.StreamFramesAsync(ct))
        {
            var header = $"\r\n--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n";
            await body.WriteAsync(System.Text.Encoding.ASCII.GetBytes(header), ct);
            await body.WriteAsync(frame, ct);
            await body.FlushAsync(ct);
        }
    }

    /// <summary>Reset encoder counters (R command).</summary>
    [HttpPost("encoders/reset")]
    public IActionResult ResetEncoders(CancellationToken ct)
    {
        _serial.ResetEncoders(ct);
        return Ok();
    }

    /// <summary>Configure encoder correction: ENC 0|1 [kp [max]].</summary>
    [HttpPost("enc")]
    public IActionResult SetEncoderConfig([FromBody] EncoderConfig config, CancellationToken ct)
    {
        _serial.SendEncoderConfig(config.Enabled, config.Kp, config.Max, ct);
        return Ok();
    }
}

public record DriveRequest(int Bearing, int Velocity);
public record LcdRequest(string? Line1, string? Line2);
public record LcdAutoRequest(bool Enabled);
public record IrRequest(bool On);
public record CameraQualityRequest(string? Preset);
