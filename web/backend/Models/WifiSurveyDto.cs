namespace RoverOperatorApi.Models;

public record WifiApDto(
    string Bssid,
    string? Ssid,
    int? FreqMHz,
    double? SignalDbm);

public record WifiSurveyDto(
    string? Interface,
    string? CurrentBssid,
    string? CurrentSsid,
    IReadOnlyList<WifiApDto> AccessPoints,
    string? Error);
