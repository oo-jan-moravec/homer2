using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;
using RoverOperatorApi.Services;

namespace RoverOperatorApi.Hubs;

/// <summary>
/// JPEG frames over SignalR (WebSocket). Used instead of HTTP MJPEG for paths that buffer
/// long-lived responses (e.g. Cloudflare Tunnel); LAN can use either.
/// </summary>
public sealed class CameraHub : Hub
{
    private readonly ICameraStreamService _camera;

    public CameraHub(ICameraStreamService camera) => _camera = camera;

    /// <summary>Streams base64 JPEG frames; client uses <c>connection.stream('streamCamera')</c>.</summary>
    public async IAsyncEnumerable<string> StreamCamera(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_camera.IsAvailable)
            yield break;

        await foreach (var frame in _camera.StreamFramesAsync(cancellationToken))
            yield return Convert.ToBase64String(frame);
    }
}
