using Microsoft.AspNetCore.SignalR;
using RoverOperatorApi.Services;

namespace RoverOperatorApi.Hubs;

/// <summary>
/// Bidirectional audio: rover mic stream to operator, operator voice to rover speakers.
/// </summary>
public class SoundHub : Hub
{
    private readonly IAudioStreamService _audio;
    private readonly ILogger<SoundHub> _logger;

    public SoundHub(IAudioStreamService audio, ILogger<SoundHub> logger)
    {
        _audio = audio;
        _logger = logger;
    }

    public Task SubscribeToMicStream()
    {
        if (!_audio.IsAvailable) return Task.CompletedTask;

        _audio.SubscribeMic(Context.ConnectionId);
        return Task.CompletedTask;
    }

    public Task UnsubscribeFromMicStream()
    {
        _audio.UnsubscribeMic(Context.ConnectionId);
        return Task.CompletedTask;
    }

    private static long _hubSpeakerCount;

    public Task SendSpeakerChunk(string chunkBase64)
    {
        if (!_audio.IsAvailable || string.IsNullOrEmpty(chunkBase64))
            return Task.CompletedTask;
        try
        {
            var chunk = Convert.FromBase64String(chunkBase64);
            if (chunk.Length > 8192) Array.Resize(ref chunk, 8192);
            var n = Interlocked.Increment(ref _hubSpeakerCount);
            if (n <= 3 || n % 50 == 0)
            {
                var s0 = chunk.Length >= 2 ? (short)(chunk[0] | (chunk[1] << 8)) : (short)0;
                _logger.LogInformation("SoundHub: received chunk #{N} {Bytes}B, first sample {S0}", n, chunk.Length, s0);
            }
            _audio.SendSpeakerChunk(chunk);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SendSpeakerChunk failed");
        }
        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _audio.UnsubscribeMic(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
