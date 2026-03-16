namespace RoverOperatorApi.Services;

public interface IVideoQualityService
{
    string Preset { get; }
    void SetPreset(string preset);
    (int Width, int Height, int Quality) GetRpicamArgs();
}

/// <summary>
/// Stores video quality preset for MJPEG stream. Affects resolution and JPEG quality.
/// Default 480p for Pi Zero (512MB RAM) - use console to raise if needed.
/// </summary>
public sealed class VideoQualityService : IVideoQualityService
{
    private string _preset = "480p";
    private readonly object _lock = new();

    public string Preset
    {
        get { lock (_lock) return _preset; }
    }

    public void SetPreset(string preset)
    {
        var normalized = preset?.ToLowerInvariant()?.Trim() ?? "480p";
        if (normalized is not ("1080p" or "720p" or "480p" or "240p"))
            normalized = "480p";
        lock (_lock) _preset = normalized;
    }

    public (int Width, int Height, int Quality) GetRpicamArgs()
    {
        var p = Preset;
        return p switch
        {
            "240p" => (320, 240, 35),
            "720p" => (1280, 720, 45),
            "1080p" => (1920, 1080, 50),
            _ => (640, 480, 40) // 480p default for Pi Zero
        };
    }
}
