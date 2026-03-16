using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace RoverOperatorApi.Services;

public interface ICameraStreamService
{
    bool IsAvailable { get; }
    IAsyncEnumerable<byte[]> StreamFramesAsync(CancellationToken ct = default);
}

/// <summary>
/// Streams MJPEG from rpicam-vid. Broadcasts to all subscribers. Starts camera on first subscriber, stops when last disconnects.
/// </summary>
public sealed class CameraStreamService : ICameraStreamService, IAsyncDisposable
{
    private readonly ILogger<CameraStreamService> _logger;
    private readonly IVideoQualityService _quality;
    private readonly string? _vidExe;
    private readonly CancellationTokenSource _globalCts = new();
    private Process? _process;
    private readonly object _processLock = new();
    private int _subscriberCount;
    private readonly ConcurrentDictionary<Channel<byte[]>, byte> _channels = new();

    public bool IsAvailable => _vidExe != null;

    public CameraStreamService(ILogger<CameraStreamService> logger, IVideoQualityService quality)
    {
        _logger = logger;
        _quality = quality;
        _vidExe = FindRpicamVid();
    }

    private static string? FindRpicamVid()
    {
        foreach (var exe in new[] { "rpicam-vid", "libcamera-vid" })
        {
            try
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
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

    public async IAsyncEnumerable<byte[]> StreamFramesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_vidExe == null)
        {
            _logger.LogWarning("Camera stream not available (rpicam-vid not found)");
            yield break;
        }

        var channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _channels.TryAdd(channel, 0);

        lock (_processLock)
        {
            _subscriberCount++;
            if (_subscriberCount == 1)
                StartProcess();
        }

        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(ct))
                yield return frame;
        }
        finally
        {
            _channels.TryRemove(channel, out _);
            channel.Writer.Complete();
            lock (_processLock)
            {
                _subscriberCount--;
                if (_subscriberCount <= 0)
                    StopProcess();
            }
        }
    }

    private void StartProcess()
    {
        if (_process != null) return;

        var (w, h, q) = _quality.GetRpicamArgs();

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _vidExe!,
                Arguments = $"-n -t 0 -o - --codec mjpeg --width {w} --height {h} -q {q}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        _process.Start();

        _ = Task.Run(async () =>
        {
            try
            {
                await ReadMjpegStreamAsync(_process.StandardOutput.BaseStream, _globalCts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Camera stream read error");
            }
        });
    }

    private async Task ReadMjpegStreamAsync(Stream stdout, CancellationToken ct)
    {
        const byte jpegStart1 = 0xFF;
        const byte jpegStart2 = 0xD8;
        const byte jpegEnd1 = 0xFF;
        const byte jpegEnd2 = 0xD9;

        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        var pending = new List<byte>();
        try
        {
            while (!ct.IsCancellationRequested && _process is { HasExited: false })
            {
                var n = await stdout.ReadAsync(buffer, ct);
                if (n <= 0) break;

                for (var i = 0; i < n; i++)
                    pending.Add(buffer[i]);

                while (pending.Count >= 2)
                {
                    if (pending[0] == jpegStart1 && pending[1] == jpegStart2)
                    {
                        var frameStart = 0;
                        var foundEnd = false;
                        for (var j = 2; j < pending.Count - 1; j++)
                        {
                            if (pending[j] == jpegEnd1 && pending[j + 1] == jpegEnd2)
                            {
                                var frameLen = j + 2 - frameStart;
                                if (frameLen > 100)
                                {
                                    var frame = new byte[frameLen];
                                    for (var k = 0; k < frameLen; k++)
                                        frame[k] = pending[frameStart + k];
                                    BroadcastFrame(frame);
                                    foundEnd = true;
                                }
                                pending.RemoveRange(0, j + 2);
                                break;
                            }
                        }
                        if (!foundEnd) break;
                    }
                    else
                    {
                        pending.RemoveAt(0);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void BroadcastFrame(byte[] frame)
    {
        foreach (var ch in _channels.Keys)
        {
            try { ch.Writer.TryWrite((byte[])frame.Clone()); } catch { }
        }
    }

    private void StopProcess()
    {
        try
        {
            _process?.Kill();
            _process?.Dispose();
        }
        catch { }
        _process = null;
        _subscriberCount = 0;
        foreach (var ch in _channels.Keys)
            ch.Writer.Complete();
        _channels.Clear();
    }

    public async ValueTask DisposeAsync() => await _globalCts.CancelAsync();
}
