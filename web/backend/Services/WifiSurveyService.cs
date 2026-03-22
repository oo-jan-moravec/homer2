using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using RoverOperatorApi.Models;

namespace RoverOperatorApi.Services;

public interface IWifiSurveyService
{
    Task<WifiSurveyDto> GetSurveyAsync(CancellationToken cancellationToken = default);
}

public class WifiSurveyService : IWifiSurveyService
{
    private static readonly Regex RxBssHeader = new(@"^BSS ([0-9a-fA-F:]+)", RegexOptions.Compiled);
    private static readonly Regex RxSsid = new(@"^\s+SSID:\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex RxSignal = new(@"^\s+signal:\s*(-?[0-9]+(?:\.[0-9]+)?)\s+dBm", RegexOptions.Compiled);
    private static readonly Regex RxFreq = new(@"^\s+freq:\s*([0-9]+)", RegexOptions.Compiled);
    private static readonly Regex RxConnected = new(@"Connected to\s+([0-9a-fA-F:]+)", RegexOptions.Compiled);
    private static readonly Regex RxLinkSsid = new(@"^\s+SSID:\s*(.*)$", RegexOptions.Compiled);

    private static readonly string[] IwPaths = ["/usr/sbin/iw", "/sbin/iw"];

    public async Task<WifiSurveyDto> GetSurveyAsync(CancellationToken cancellationToken = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new WifiSurveyDto(null, null, null, [], "WiFi survey is only available on the rover (Linux).");
        }

        var iw = ResolveIwPath();
        if (iw == null)
            return new WifiSurveyDto(null, null, null, [], "The `iw` tool was not found (install iw or use a full Raspberry Pi OS image).");

        var iface = GuessWirelessInterface();
        if (string.IsNullOrEmpty(iface))
            return new WifiSurveyDto(null, null, null, [], "No wireless interface found.");

        string? curBssid = null;
        string? curSsid = null;
        try
        {
            var linkOut = await RunProcessAsync(iw, ["dev", iface, "link"], TimeSpan.FromSeconds(5), cancellationToken);
            if (linkOut.ExitCode == 0 && !string.IsNullOrWhiteSpace(linkOut.Stdout))
            {
                var m = RxConnected.Match(linkOut.Stdout);
                if (m.Success)
                    curBssid = NormalizeBssid(m.Groups[1].Value);
                foreach (var line in linkOut.Stdout.Split('\n'))
                {
                    var sm = RxLinkSsid.Match(line);
                    if (sm.Success)
                    {
                        curSsid = UnescapeIwString(sm.Groups[1].Value.Trim());
                        if (string.IsNullOrEmpty(curSsid) || curSsid == "\0")
                            curSsid = null;
                    }
                }
            }
        }
        catch
        {
            // ignore link parse errors
        }

        ProcessResult scanOut;
        try
        {
            scanOut = await RunProcessAsync(iw, ["dev", iface, "scan"], TimeSpan.FromSeconds(28), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WifiSurveyDto(iface, curBssid, curSsid, [], $"WiFi scan failed: {ex.Message}");
        }

        if (scanOut.ExitCode != 0)
        {
            var hint = scanOut.Stderr.Contains("Operation not permitted", StringComparison.Ordinal) ||
                       scanOut.Stderr.Contains("Permission denied", StringComparison.Ordinal)
                ? " Permission denied: grant CAP_NET_ADMIN to the service (see deploy/rover-operator-console.service) or run as root."
                : "";
            var err = string.IsNullOrWhiteSpace(scanOut.Stderr) ? $"exit {scanOut.ExitCode}" : scanOut.Stderr.Trim();
            return new WifiSurveyDto(iface, curBssid, curSsid, [], $"iw scan failed: {err}.{hint}");
        }

        var parsed = ParseIwScan(scanOut.Stdout);
        var deduped = DedupeBestSignal(parsed);
        deduped.Sort((a, b) => CompareSignal(b.SignalDbm, a.SignalDbm));

        return new WifiSurveyDto(iface, curBssid, curSsid, deduped, null);
    }

    private static int CompareSignal(double? a, double? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        return a.Value.CompareTo(b.Value);
    }

    private static List<WifiApDto> DedupeBestSignal(List<WifiApDto> rows)
    {
        var best = new Dictionary<string, WifiApDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Bssid)) continue;
            if (!best.TryGetValue(row.Bssid, out var existing))
            {
                best[row.Bssid] = row;
                continue;
            }
            if (CompareSignal(row.SignalDbm, existing.SignalDbm) > 0)
                best[row.Bssid] = row;
        }
        return best.Values.ToList();
    }

    internal static List<WifiApDto> ParseIwScan(string stdout)
    {
        var list = new List<WifiApDto>();
        string? bssid = null;
        string? ssid = null;
        double? signal = null;
        int? freq = null;

        void Flush()
        {
            if (string.IsNullOrEmpty(bssid)) return;
            var ssidOut = string.IsNullOrEmpty(ssid) || ssid == "\0" ? null : ssid;
            list.Add(new WifiApDto(bssid, ssidOut, freq, signal));
            bssid = null;
            ssid = null;
            signal = null;
            freq = null;
        }

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var hm = RxBssHeader.Match(line);
            if (hm.Success)
            {
                Flush();
                bssid = NormalizeBssid(hm.Groups[1].Value);
                continue;
            }

            if (bssid == null) continue;

            var sm = RxSsid.Match(line);
            if (sm.Success)
            {
                ssid = UnescapeIwString(sm.Groups[1].Value.Trim());
                continue;
            }

            var sigm = RxSignal.Match(line);
            if (sigm.Success && double.TryParse(sigm.Groups[1].Value, CultureInfo.InvariantCulture, out var dbm))
            {
                signal = dbm;
                continue;
            }

            var fm = RxFreq.Match(line);
            if (fm.Success && int.TryParse(fm.Groups[1].Value, CultureInfo.InvariantCulture, out var f))
                freq = f;
        }

        Flush();
        return list;
    }

    private static string? UnescapeIwString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s.Contains('\\', StringComparison.Ordinal))
        {
            try
            {
                return Regex.Unescape(s);
            }
            catch
            {
                return s;
            }
        }
        return s;
    }

    private static string NormalizeBssid(string mac) =>
        mac.Trim().ToLowerInvariant();

    private static string? ResolveIwPath()
    {
        foreach (var p in IwPaths)
        {
            try
            {
                if (File.Exists(p))
                    return p;
            }
            catch { }
        }
        return null;
    }

    private static string? GuessWirelessInterface()
    {
        try
        {
            var net = "/sys/class/net";
            if (!Directory.Exists(net)) return null;
            foreach (var path in Directory.GetDirectories(net))
            {
                var name = Path.GetFileName(path);
                if (name is "lo" or null or "") continue;
                if (Directory.Exists(Path.Combine(path, "wireless")))
                    return name;
            }
        }
        catch { }

        try
        {
            var lines = File.ReadAllLines("/proc/net/wireless");
            for (var i = 2; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                var colon = line.IndexOf(':');
                if (colon > 0)
                    return line[..colon];
            }
        }
        catch { }

        return null;
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string[] args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch { }

            await proc.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException($"`{fileName}` exceeded {timeout.TotalSeconds}s.");
        }

        return new ProcessResult(proc.ExitCode, await stdoutTask, await stderrTask);
    }

    private readonly record struct ProcessResult(int ExitCode, string Stdout, string Stderr);
}
