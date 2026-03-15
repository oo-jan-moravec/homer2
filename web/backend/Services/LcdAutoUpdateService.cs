using RoverOperatorApi.Models;

namespace RoverOperatorApi.Services;

/// <summary>
/// Holds the LCD auto-update enabled state. Default: enabled.
/// </summary>
public interface ILcdAutoUpdateService
{
    bool Enabled { get; set; }
}

public sealed class LcdAutoUpdateService : ILcdAutoUpdateService
{
    public bool Enabled { get; set; } = true;
}
