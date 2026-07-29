using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pinscreen2.App;

/// <summary>One game's outstanding work, for the dashboard's incoming list.</summary>
public record PendingGameDto(string Name, int Files, long Bytes);

/// <summary>Snapshot this screen reports to the server for the dashboard.</summary>
public class DeviceStatusReport
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public int CachedFiles { get; set; }
    public long CachedBytes { get; set; }
    public long FreeBytes { get; set; }
    public string SyncState { get; set; } = "idle";   // idle | syncing | error
    public string SyncMessage { get; set; } = "";
    public int SyncFilesDone { get; set; }
    public int SyncFilesTotal { get; set; }

    /// <summary>Game folder of the file currently downloading.</summary>
    public string SyncGame { get; set; } = "";
    /// <summary>Bare filename currently downloading.</summary>
    public string SyncFile { get; set; } = "";
    /// <summary>Everything this sync set out to fetch, grouped by game.</summary>
    public List<PendingGameDto> PendingGames { get; set; } = new();
}

/// <summary>
/// Keeps a Server-Sent Events connection open to the library server so the
/// server can tell this screen to sync, and reports status back for the
/// management dashboard.
///
/// The link is expected to be unreliable (Tailscale DERP relays, kiosk boxes
/// losing wifi, the server itself restarting), so the connect loop reconnects
/// forever with backoff and never surfaces a failure to the UI -- a pinscreen
/// that cannot reach the server should just keep playing what it has.
/// </summary>
public class DeviceAgent : IDisposable
{
    private readonly string _baseUrl;
    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly string _version;

    // SSE must never time out; status posts should fail fast.
    private readonly HttpClient _stream = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _post = new() { Timeout = TimeSpan.FromSeconds(15) };

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Raised when the server pushes a sync command.</summary>
    public Func<Task>? SyncRequested { get; set; }

    /// <summary>Called to build the periodic status heartbeat.</summary>
    public Func<DeviceStatusReport>? StatusProvider { get; set; }

    public bool IsConnected { get; private set; }

    public DeviceAgent(string baseUrl, string deviceId, string deviceName, string version)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _deviceId = deviceId;
        _deviceName = deviceName;
        _version = version;
    }

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ConnectLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _loop = null;
        IsConnected = false;
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        var maxBackoff = TimeSpan.FromSeconds(30);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunStreamAsync(ct);
                // Clean close (server restart / shutdown) -- retry promptly.
                backoff = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"DeviceAgent: connection failed ({ex.Message}); retrying in {backoff.TotalSeconds:0}s");
            }
            finally { IsConnected = false; }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(backoff, ct); } catch { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(maxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
        }
    }

    private async Task RunStreamAsync(CancellationToken ct)
    {
        var url = $"{_baseUrl}/events?deviceId={Uri.EscapeDataString(_deviceId)}" +
                  $"&name={Uri.EscapeDataString(_deviceName)}" +
                  $"&version={Uri.EscapeDataString(_version)}";

        using var resp = await _stream.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        IsConnected = true;
        Console.WriteLine($"DeviceAgent: connected to {_baseUrl} as '{_deviceName}'");

        // Announce current state right away so the dashboard is populated the
        // moment a screen comes online.
        _ = ReportAsync(null, ct);

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? evt = null;
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;               // server closed the stream
            if (line.Length == 0) { evt = null; continue; }
            if (line[0] == ':') continue;          // keepalive comment

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                evt = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (string.Equals(evt, "sync", StringComparison.Ordinal))
                {
                    Console.WriteLine("DeviceAgent: sync pushed by server");
                    var handler = SyncRequested;
                    if (handler != null)
                    {
                        // Do not block the read loop -- a sync can take a long
                        // time and we must keep reading keepalives meanwhile.
                        _ = Task.Run(async () =>
                        {
                            try { await handler(); }
                            catch (Exception ex) { Console.WriteLine($"DeviceAgent: sync handler failed: {ex.Message}"); }
                        }, ct);
                    }
                }
            }
        }
    }

    /// <summary>Posts a status snapshot. Pass null to use <see cref="StatusProvider"/>.</summary>
    public async Task ReportAsync(DeviceStatusReport? report, CancellationToken ct = default)
    {
        try
        {
            report ??= StatusProvider?.Invoke();
            if (report == null) return;
            report.Name ??= _deviceName;
            report.Version ??= _version;

            var url = $"{_baseUrl}/api/devices/{Uri.EscapeDataString(_deviceId)}/status";
            using var content = new StringContent(
                JsonSerializer.Serialize(report, JsonOpts), Encoding.UTF8, "application/json");
            using var resp = await _post.PostAsync(url, content, ct);
        }
        catch (Exception ex)
        {
            // Status is advisory; never let it disturb playback.
            Console.WriteLine($"DeviceAgent: status report failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
        try { _cts?.Dispose(); } catch { }
        try { _stream.Dispose(); } catch { }
        try { _post.Dispose(); } catch { }
    }
}
