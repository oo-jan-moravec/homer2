namespace RoverOperatorApi.Services;

public interface IVideoQualityService
{
    string Preset { get; }
    void SetPreset(string preset);
    (int Width, int Height, int Quality) GetRpicamArgs();
}

/// <summary>
/// Stores video quality preset for MJPEG stream. Affects resolution and JPEG quality.
/// </summary>
public sealed class VideoQualityService : IVideoQualityService
{
    private string _preset = "1080p";
    private readonly object _lock = new();

    public string Preset
    {
        get { lock (_lock) return _preset; }
    }

    public void SetPreset(string preset)
    {
        var normalized = preset?.ToLowerInvariant()?.Trim() ?? "1080p";
        if (normalized is not ("1080p" or "720p" or "480p" or "240p"))
            normalized = "1080p";
        lock (_lock) _preset = normalized;
    }

    public (int Width, int Height, int Quality) GetRpicamArgs()
    {
        var p = Preset;
        return p switch
        {
            "240p" => (320, 240, 35),
            "480p" => (640, 480, 40),
            "720p" => (1280, 720, 45),
            _ => (1920, 1080, 50) // 1080p default
        };
    }
}
