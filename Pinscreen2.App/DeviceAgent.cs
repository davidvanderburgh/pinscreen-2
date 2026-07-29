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

/// <summary>A finished sync, appended to the server-side history for this screen.</summary>
public record CompletedSyncDto(int FilesDownloaded, long BytesDownloaded, int FilesFailed, List<string> Games);

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

    /// <summary>
    /// What this screen is missing relative to the server, grouped by game.
    /// Null means "not measured in this report" and leaves the server's copy
    /// alone; an empty list positively means "up to date".
    /// </summary>
    public List<PendingGameDto>? PendingGames { get; set; }
    public int PendingFiles { get; set; }
    public long PendingBytes { get; set; }
    /// <summary>When the diff above was computed, so the dashboard can age it.</summary>
    public DateTimeOffset? PendingCheckedAt { get; set; }

    /// <summary>Set once when a sync finishes, to append to the history.</summary>
    public CompletedSyncDto? CompletedSync { get; set; }

    /// <summary>How many times the clock popup has been re-anchored after a display change.</summary>
    public int ClockReplacements { get; set; }
    /// <summary>Current display geometry signature, for diagnosing clock placement.</summary>
    public string DisplayGeometry { get; set; } = "";
    /// <summary>When the display mode last actually changed (monitor wake, mode switch).</summary>
    public DateTimeOffset? LastResolutionChangeAt { get; set; }

    // App-update progress for a server-pushed update.
    /// <summary>idle | checking | downloading | installing | error | uptodate</summary>
    public string UpdateState { get; set; } = "idle";
    public string UpdateMessage { get; set; } = "";
    public int UpdatePercent { get; set; }
    /// <summary>When the state above was set, so the dashboard can age a stale error.</summary>
    public DateTimeOffset? UpdateStateAt { get; set; }
    /// <summary>False means a push update may stall on a UAC prompt nobody can click.</summary>
    public bool IsElevated { get; set; }

    // Tailscale health, so a link that is about to strand a screen is visible
    // before it does.
    public bool TailscaleInstalled { get; set; }
    public bool TailscaleHealthy { get; set; }
    public string TailscaleState { get; set; } = "";
    public int TailscaleRecoveries { get; set; }
    public string TailscaleLastAction { get; set; } = "";
    public DateTimeOffset? TailscaleLastActionAt { get; set; }

    // UI-thread stalls. "Not responding" is otherwise invisible from anywhere
    // except standing in front of the screen.
    public int UiStalls { get; set; }
    public double WorstUiStallSeconds { get; set; }
    public DateTimeOffset? LastUiStallAt { get; set; }
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

    /// <summary>
    /// Raised when the server says the library changed but is not asking for a
    /// sync, and once per successful connect. The screen answers by re-diffing
    /// itself so the dashboard knows whether it is behind.
    /// </summary>
    public Func<Task>? CheckRequested { get; set; }

    /// <summary>Raised when the server pushes an app-update command.</summary>
    public Func<Task>? UpdateRequested { get; set; }

    /// <summary>
    /// Raised when the dashboard asks for a Tailscale restart. Only reaches
    /// screens still able to talk to the server -- a screen whose Tailscale is
    /// already down relies on its own watchdog instead.
    /// </summary>
    public Func<Task>? TailscaleRestartRequested { get; set; }

    /// <summary>
    /// Raised when the dashboard renames this screen. Receives the new name so
    /// the app can persist it -- otherwise the next heartbeat would report the
    /// machine name again.
    /// </summary>
    public Func<string, Task>? RenameRequested { get; set; }

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
        // moment a screen comes online, then re-diff against the server so its
        // "behind by N" figure isn't left over from before the screen was off.
        _ = ReportAsync(null, ct);
        Fire(CheckRequested, "connect", ct);

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
                // Never block the read loop -- these handlers can run for
                // minutes and we must keep reading keepalives meanwhile.
                if (string.Equals(evt, "sync", StringComparison.Ordinal))
                {
                    Console.WriteLine("DeviceAgent: sync pushed by server");
                    Fire(SyncRequested, "sync", ct);
                }
                else if (string.Equals(evt, "check", StringComparison.Ordinal))
                {
                    Console.WriteLine("DeviceAgent: library changed, re-checking");
                    Fire(CheckRequested, "check", ct);
                }
                else if (string.Equals(evt, "update", StringComparison.Ordinal))
                {
                    Console.WriteLine("DeviceAgent: app update pushed by server");
                    Fire(UpdateRequested, "update", ct);
                }
                else if (string.Equals(evt, "tailscale-restart", StringComparison.Ordinal))
                {
                    Console.WriteLine("DeviceAgent: Tailscale restart pushed by server");
                    Fire(TailscaleRestartRequested, "tailscale-restart", ct);
                }
                else if (string.Equals(evt, "rename", StringComparison.Ordinal))
                {
                    var name = ReadStringField(line["data:".Length..].Trim(), "name");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        Console.WriteLine($"DeviceAgent: renamed to '{name}' by server");
                        var handler = RenameRequested;
                        if (handler != null)
                        {
                            _ = Task.Run(async () =>
                            {
                                try { await handler(name!); }
                                catch (Exception ex) { Console.WriteLine($"DeviceAgent: rename handler failed: {ex.Message}"); }
                            }, ct);
                        }
                    }
                }
            }
        }
    }

    private static string? ReadStringField(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(field, out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    private static void Fire(Func<Task>? handler, string what, CancellationToken ct)
    {
        if (handler == null) return;
        _ = Task.Run(async () =>
        {
            try { await handler(); }
            catch (Exception ex) { Console.WriteLine($"DeviceAgent: {what} handler failed: {ex.Message}"); }
        }, ct);
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
