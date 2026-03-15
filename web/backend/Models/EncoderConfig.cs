namespace RoverOperatorApi.Models;

/// <summary>
/// Encoder correction: ENC 0|1 [kp [max]].
/// Bearing 0 or 180 only; kp=1-100, max=1-255.
/// </summary>
public record EncoderConfig(bool Enabled, int? Kp = null, int? Max = null);
