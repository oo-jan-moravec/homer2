using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using RoverOperatorApi.Hubs;

namespace RoverOperatorApi.Services;

public interface IAudioStreamService
{
    bool IsAvailable { get; }
    void SubscribeMic(string connectionId);
    void UnsubscribeMic(string connectionId);
    void SendSpeakerChunk(byte[] chunk);
}

/// <summary>
/// Streams rover USB mic (arecord) to subscribers. Receives voice chunks and plays to rover speakers (aplay).
/// Format: 16kHz mono S16_LE. Chunk: 2048 bytes (64ms). Only on Linux with USB sound card.
/// </summary>
public sealed class AudioStreamService : IAudioStreamService, IDisposable
{
    private readonly ILogger<AudioStreamService> _logger;
    private readonly IHubContext<SoundHub> _hubContext;
    private readonly string? _recordDevice;
    private readonly string? _playbackDevice;
    private Process? _micProcess;
    private Process? _speakerProcess;
    private readonly ConcurrentDictionary<string, byte> _micSubscribers = new();
    private readonly object _micLock = new();
    private readonly object _speakerLock = new();
    private Stream? _speakerStdin;
    private Task? _micReadTask;

    private const int SampleRate = 16000;
    private const int ChunkSamples = 1024; // 64ms
    private const int ChunkBytes = ChunkSamples * 2; // S16_LE

    public bool IsAvailable { get; }

    public AudioStreamService(ILogger<AudioStreamService> logger, IHubContext<SoundHub> hubContext)
    {
        _logger = logger;
        _hubContext = hubContext;
        (_recordDevice, _playbackDevice) = FindAlsaDevices();
        IsAvailable = _recordDevice != null && _playbackDevice != null;
    }

    private static (string? record, string? playback) FindAlsaDevices()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return (null, null);

        var record = AlsaListPreferredDevice("arecord");
        var playback = AlsaListPreferredDevice("aplay");
        if (record == null || playback == null)
            return (null, null);

        return (record, playback);
    }

    /// <summary>
    /// Parses <c>arecord -l</c> / <c>aplay -l</c> output. Prefers a line mentioning USB; otherwise first card/device pair.
    /// </summary>
    private static string? AlsaListPreferredDevice(string command)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = "-l",
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                }
            };
            p.Start();
            var outStr = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(outStr))
                return null;

            return ParsePreferredPlughw(outStr);
        }
        catch
        {
            return null;
        }
    }

    private static readonly Regex AlsaCardDeviceLine = new(
        @"card\s+(\d+):\s*[^\n]*,\s*device\s+(\d+):",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? ParsePreferredPlughw(string listOutput)
    {
        int? firstCard = null, firstDev = null;
        int? usbCard = null, usbDev = null;

        foreach (var line in listOutput.Split('\n'))
        {
            var m = AlsaCardDeviceLine.Match(line);
            if (!m.Success)
                continue;

            var card = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var dev = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            firstCard ??= card;
            firstDev ??= dev;

            if (line.Contains("USB", StringComparison.OrdinalIgnoreCase))
            {
                usbCard = card;
                usbDev = dev;
            }
        }

        var c = usbCard ?? firstCard;
        var d = usbDev ?? firstDev;
        return c != null && d != null ? $"plughw:{c},{d}" : null;
    }

    public void SubscribeMic(string connectionId)
    {
        _micSubscribers.TryAdd(connectionId, 0);
        lock (_micLock)
        {
            if (_micProcess == null)
                StartMicProcess();
        }
    }

    public void UnsubscribeMic(string connectionId)
    {
        _micSubscribers.TryRemove(connectionId, out _);
        lock (_micLock)
        {
            if (_micSubscribers.IsEmpty && _micProcess != null)
                StopMicProcess();
        }
    }

    private void StartMicProcess()
    {
        if (_recordDevice == null || _micProcess != null) return;

        _micProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "arecord",
                Arguments = $"-D {_recordDevice} -q -t raw -f S16_LE -r {SampleRate} -c 1 -",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        _micProcess.Start();

        _micReadTask = Task.Run(async () =>
        {
            var buffer = new byte[ChunkBytes];
            try
            {
                var stdout = _micProcess!.StandardOutput.BaseStream;
                while (_micProcess is { HasExited: false } && !_micSubscribers.IsEmpty)
                {
                    var total = 0;
                    while (total < ChunkBytes)
                    {
                        var n = await stdout.ReadAsync(buffer.AsMemory(total, ChunkBytes - total));
                        if (n <= 0) return;
                        total += n;
                    }

                    var chunk = (byte[])buffer.Clone();
                    foreach (var connectionId in _micSubscribers.Keys.ToArray())
                    {
                        try
                        {
                            await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveMicChunk", chunk);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Mic stream read");
            }
        });

        _logger.LogInformation("Mic stream started");
    }

    private void StopMicProcess()
    {
        try
        {
            _micProcess?.Kill();
            _micProcess?.Dispose();
        }
        catch { }
        _micProcess = null;
        _micReadTask = null;
        _logger.LogInformation("Mic stream stopped");
    }

    private long _speakerChunkCount;

    public void SendSpeakerChunk(byte[] chunk)
    {
        if (_playbackDevice == null || chunk.Length == 0) return;

        lock (_speakerLock)
        {
            if (_speakerProcess == null)
                StartSpeakerProcess();
        }

        try
        {
            var len = Math.Min(chunk.Length, ChunkBytes * 4);
            _speakerStdin?.Write(chunk, 0, len);
            _speakerStdin?.Flush();
            _speakerChunkCount++;
            if (_speakerChunkCount <= 3 || _speakerChunkCount % 50 == 0)
                _logger.LogInformation("Speaker: wrote chunk #{Count}, {Bytes}B", _speakerChunkCount, len);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Speaker write failed");
        }
    }

    private void StartSpeakerProcess()
    {
        if (_playbackDevice == null || _speakerProcess != null) return;

        _speakerProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "aplay",
                Arguments = $"-D {_playbackDevice} -q -t raw -f S16_LE -r {SampleRate} -c 1 -B 200000 -",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true
            }
        };
        _speakerProcess.Start();
        _speakerStdin = _speakerProcess.StandardInput.BaseStream;
        _logger.LogInformation("Speaker playback started");
    }

    public void Dispose()
    {
        StopMicProcess();
        lock (_speakerLock)
        {
            try
            {
                _speakerStdin?.Close();
                _speakerProcess?.Kill();
                _speakerProcess?.Dispose();
            }
            catch { }
            _speakerProcess = null;
            _speakerStdin = null;
        }
    }
}
