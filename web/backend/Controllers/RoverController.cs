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

    public RoverController(
        IRoverSerialService serial,
        ILcdService lcd,
        IIrService ir,
        ICameraService camera)
    {
        _serial = serial;
        _lcd = lcd;
        _ir = ir;
        _camera = camera;
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
            timestamp = DateTime.UtcNow
        });
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

    /// <summary>Capture camera image. Returns JPEG.</summary>
    [HttpGet("camera")]
    public async Task<IActionResult> CaptureCamera(CancellationToken ct)
    {
        var bytes = await _camera.CaptureAsync(ct);
        if (bytes == null || bytes.Length == 0)
            return StatusCode(503, "Camera capture failed or not available");
        return File(bytes, "image/jpeg");
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

public record LcdRequest(string? Line1, string? Line2);
public record IrRequest(bool On);
