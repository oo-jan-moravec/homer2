namespace RoverOperatorApi.Services;

public interface ICameraService
{
    bool IsAvailable { get; }
    Task<byte[]?> CaptureAsync(CancellationToken ct = default);
}

/// <summary>
/// Captures image via rpicam-still / libcamera-still. Returns JPEG bytes.
/// </summary>
public sealed class CameraService : ICameraService
{
    private readonly ILogger<CameraService> _logger;
    private readonly string? _exePath;

    public bool IsAvailable => _exePath != null;

    public CameraService(ILogger<CameraService> logger)
    {
        _logger = logger;
        _exePath = FindCameraExe();
    }

    private static string? FindCameraExe()
    {
        foreach (var exe in new[] { "rpicam-still", "libcamera-still" })
        {
            try
            {
                using var proc = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "which",
                        Arguments = exe,
                        UseShellExecute = false,
                        RedirectStandardOutput = true
                    }
                };
                proc.Start();
                var path = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(1000);
                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(path))
                    return exe;
            }
            catch { }
        }
        return null;
    }

    public async Task<byte[]?> CaptureAsync(CancellationToken ct = default)
    {
        if (_exePath == null)
        {
            _logger.LogWarning("Camera not available");
            return null;
        }

        var outPath = Path.Combine(Path.GetTempPath(), $"rover-cam-{Guid.NewGuid():N}.jpg");
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _exePath,
                    Arguments = $"-o \"{outPath}\" -t 1",
                    UseShellExecute = false,
                    RedirectStandardError = true
                }
            };
            proc.Start();
            var err = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
            {
                _logger.LogWarning("Camera failed: {Err}", err);
                return null;
            }
            return File.Exists(outPath) ? await File.ReadAllBytesAsync(outPath, ct) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Camera capture failed");
            return null;
        }
        finally
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
        }
    }
}
