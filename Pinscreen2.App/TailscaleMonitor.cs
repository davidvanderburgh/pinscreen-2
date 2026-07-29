using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pinscreen2.App;

/// <summary>Health snapshot of the local Tailscale install.</summary>
public class TailscaleHealth
{
    public bool Installed { get; set; }
    /// <summary>Backend state reported by the CLI: Running, Stopped, NeedsLogin, NoState…</summary>
    public string BackendState { get; set; } = "";
    public bool SelfOnline { get; set; }
    public bool DaemonRunning { get; set; }
    public bool TrayRunning { get; set; }
    public string Error { get; set; } = "";

    /// <summary>Healthy enough to carry traffic.</summary>
    public bool IsHealthy =>
        Installed && string.IsNullOrEmpty(Error) && DaemonRunning &&
        (BackendState.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
         BackendState.Equals("Starting", StringComparison.OrdinalIgnoreCase));

    public string Summary => !Installed ? "not installed"
        : !string.IsNullOrEmpty(Error) ? Error
        : $"{(string.IsNullOrEmpty(BackendState) ? "unknown" : BackendState)}" +
          (DaemonRunning ? "" : ", daemon down") +
          (TrayRunning ? "" : ", tray down") +
          (SelfOnline ? "" : ", offline");
}

/// <summary>
/// Watches the local Tailscale install and restarts it when it dies.
///
/// This has to live on the screen rather than the server. A screen that reaches
/// the server over Tailscale loses its connection the moment Tailscale stops --
/// it simply goes offline on the dashboard, and a "restart Tailscale" command
/// has no transport left to arrive on. Remote control is useful for a screen
/// that is degraded but still reachable, or one on the LAN; self-healing is what
/// covers the case that actually strands a machine.
///
/// Status queries need no elevation. Restarting the *service* does, so recovery
/// climbs a ladder from least to most privileged and reports which rung worked.
/// </summary>
public static class TailscaleMonitor
{
    private static readonly string[] CliCandidates =
    {
        @"C:\Program Files\Tailscale\tailscale.exe",
        @"C:\Program Files (x86)\Tailscale\tailscale.exe",
    };

    public static string? FindCli()
    {
        foreach (var p in CliCandidates)
            if (File.Exists(p)) return p;
        return null;
    }

    private static string? TrayExe()
    {
        var cli = FindCli();
        if (cli == null) return null;
        var tray = Path.Combine(Path.GetDirectoryName(cli)!, "tailscale-ipn.exe");
        return File.Exists(tray) ? tray : null;
    }

    private static bool ProcessRunning(string name)
    {
        try { return Process.GetProcessesByName(name).Length > 0; }
        catch { return false; }
    }

    public static async Task<TailscaleHealth> CheckAsync(CancellationToken ct = default)
    {
        var health = new TailscaleHealth();
        var cli = FindCli();
        if (cli == null) return health; // Installed stays false

        health.Installed = true;
        health.DaemonRunning = ProcessRunning("tailscaled");
        health.TrayRunning = ProcessRunning("tailscale-ipn");

        try
        {
            var (exit, stdout, _) = await RunAsync(cli, "status --json", TimeSpan.FromSeconds(15), ct);
            if (exit != 0 && string.IsNullOrWhiteSpace(stdout))
            {
                health.Error = $"status exited {exit}";
                return health;
            }
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.TryGetProperty("BackendState", out var bs)) health.BackendState = bs.GetString() ?? "";
            if (root.TryGetProperty("Self", out var self) && self.ValueKind == JsonValueKind.Object &&
                self.TryGetProperty("Online", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False)
                health.SelfOnline = on.GetBoolean();
        }
        catch (Exception ex) { health.Error = ex.Message; }

        return health;
    }

    /// <summary>
    /// Attempts recovery, least-privileged first. Returns a human-readable
    /// account of what was tried and what happened.
    /// </summary>
    public static async Task<string> TryRecoverAsync(TailscaleHealth health, CancellationToken ct = default)
    {
        var cli = FindCli();
        if (cli == null) return "Tailscale is not installed.";

        var steps = new System.Collections.Generic.List<string>();

        // 1. Tray/GUI gone but daemon alive -- cheapest fix, needs no admin.
        if (!health.TrayRunning)
        {
            var tray = TrayExe();
            if (tray != null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = tray, UseShellExecute = true });
                    steps.Add("relaunched tray");
                }
                catch (Exception ex) { steps.Add($"tray relaunch failed: {ex.Message}"); }
            }
        }

        // 2. Daemon down: the service has to come back, which needs admin.
        if (!health.DaemonRunning)
        {
            var (exit, _, err) = await RunAsync("sc.exe", "start Tailscale", TimeSpan.FromSeconds(30), ct);
            steps.Add(exit == 0
                ? "started Tailscale service"
                : $"service start failed ({exit}{(string.IsNullOrWhiteSpace(err) ? "" : ": " + err.Trim())})" +
                  (exit == 5 ? " — needs admin" : ""));
        }

        // 3. Daemon up but backend not running (stopped / logged out).
        if (health.DaemonRunning && !health.IsHealthy)
        {
            var (exit, _, err) = await RunAsync(cli, "up", TimeSpan.FromSeconds(45), ct);
            steps.Add(exit == 0 ? "ran tailscale up"
                : $"tailscale up failed ({exit}{(string.IsNullOrWhiteSpace(err) ? "" : ": " + err.Trim())})");
        }

        return steps.Count == 0 ? "nothing to do" : string.Join("; ", steps);
    }

    /// <summary>Explicit restart, for the dashboard button on a still-reachable screen.</summary>
    public static async Task<string> RestartAsync(CancellationToken ct = default)
    {
        var cli = FindCli();
        if (cli == null) return "Tailscale is not installed.";

        var (stopExit, _, stopErr) = await RunAsync("sc.exe", "stop Tailscale", TimeSpan.FromSeconds(30), ct);
        if (stopExit != 0 && stopExit != 1062 /* not started */)
        {
            return $"service stop failed ({stopExit}{(string.IsNullOrWhiteSpace(stopErr) ? "" : ": " + stopErr.Trim())})" +
                   (stopExit == 5 ? " — needs admin" : "");
        }
        await Task.Delay(2000, ct);
        var (startExit, _, startErr) = await RunAsync("sc.exe", "start Tailscale", TimeSpan.FromSeconds(30), ct);
        if (startExit != 0)
            return $"service start failed ({startExit}{(string.IsNullOrWhiteSpace(startErr) ? "" : ": " + startErr.Trim())})";

        return "Tailscale service restarted.";
    }

    private static async Task<(int Exit, string Out, string Err)> RunAsync(
        string exe, string args, TimeSpan timeout, CancellationToken ct)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        proc.Start();
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try { await proc.WaitForExitAsync(timeoutCts.Token); }
        catch (OperationCanceledException)
        {
            // A hung CLI must not wedge the caller -- this runs on a timer.
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "timed out");
        }
        return (proc.ExitCode, await stdout, await stderr);
    }
}
