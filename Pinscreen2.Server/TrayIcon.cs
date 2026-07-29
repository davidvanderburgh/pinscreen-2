using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace Pinscreen2.Server;

/// <summary>
/// System-tray presence for the library server.
///
/// The server runs hidden, which is how it managed to die and stay dead for two
/// months without anyone noticing. An icon in the notification area makes it
/// visible: present means serving, gone means down (the watchdog relaunches
/// within ~10s, so a permanently missing icon is a real fault), and the tooltip
/// carries the library and screen counts.
///
/// Runs its own message pump on a dedicated STA thread so Kestrel is untouched.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly LibraryService _library;
    private readonly DeviceRegistry _devices;
    private readonly int _port;
    private readonly string _exeDir;
    private readonly Func<string, bool> _refreshAndPush;

    private NotifyIcon? _icon;
    private Thread? _thread;
    private ApplicationContext? _ctx;
    private System.Windows.Forms.Timer? _tooltipTimer;

    public TrayIcon(LibraryService library, DeviceRegistry devices, int port, string exeDir,
                    Func<string, bool> refreshAndPush)
    {
        _library = library;
        _devices = devices;
        _port = port;
        _exeDir = exeDir;
        _refreshAndPush = refreshAndPush;
    }

    private string DashboardUrl => $"http://localhost:{_port}/";

    public void Start()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "TrayIcon" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Pump()
    {
        try
        {
            var menu = new ContextMenuStrip();

            var open = new ToolStripMenuItem("Open dashboard", null, (_, __) => OpenUrl(DashboardUrl));
            open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
            menu.Items.Add(open);

            menu.Items.Add(new ToolStripMenuItem("Rescan library now", null, (_, __) => Rescan()));
            menu.Items.Add(new ToolStripMenuItem("Push sync to all screens", null, (_, __) => PushAll()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Open server log", null,
                (_, __) => OpenPath(Path.Combine(_exeDir, "server.log"))));
            menu.Items.Add(new ToolStripMenuItem("Open library folder", null,
                (_, __) => OpenPath(_library.Root)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Restart server", null, (_, __) => Restart()));
            menu.Items.Add(new ToolStripMenuItem("Stop server", null, (_, __) => Stop()));

            _icon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "Pinscreen Library",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _icon.DoubleClick += (_, __) => OpenUrl(DashboardUrl);

            UpdateTooltip();
            _tooltipTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _tooltipTimer.Tick += (_, __) => UpdateTooltip();
            _tooltipTimer.Start();

            _ctx = new ApplicationContext();
            Application.Run(_ctx);
        }
        catch (Exception ex)
        {
            // A tray icon is a convenience. Never let it take the server down --
            // notably under a SYSTEM scheduled task, where session 0 isolation
            // means there is no notification area to attach to.
            Console.Error.WriteLine($"Tray icon unavailable: {ex.Message}");
        }
    }

    private static System.Drawing.Icon LoadIcon()
    {
        using var s = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Pinscreen2.Server.favicon.ico");
        return s != null ? new System.Drawing.Icon(s) : System.Drawing.SystemIcons.Application;
    }

    private void UpdateTooltip()
    {
        try
        {
            if (_icon == null) return;
            var online = _devices.OnlineCount;
            // NotifyIcon.Text is capped at 63 characters; anything longer throws.
            var text = _library.IsReady
                ? $"Pinscreen Library\n{_library.FileCount:N0} videos · {online} screen{(online == 1 ? "" : "s")} online"
                : "Pinscreen Library\nScanning library…";
            _icon.Text = text.Length > 63 ? text[..63] : text;
        }
        catch { }
    }

    private void Rescan()
    {
        Task.Run(() =>
        {
            var changed = _refreshAndPush("tray-rescan");
            Notify(changed
                ? $"Library changed — {_library.FileCount:N0} videos."
                : "No changes found.");
        });
    }

    private void PushAll()
    {
        var n = _devices.Broadcast("sync", new { reason = "tray-manual", at = DateTimeOffset.UtcNow });
        Notify(n > 0
            ? $"Sync pushed to {n} screen{(n == 1 ? "" : "s")}."
            : "No screens are online.");
    }

    private void Notify(string message)
    {
        try
        {
            if (_icon == null) return;
            _icon.BalloonTipTitle = "Pinscreen Library";
            _icon.BalloonTipText = message;
            _icon.ShowBalloonTip(4000);
        }
        catch { }
        Console.WriteLine($"Tray: {message}");
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { Console.Error.WriteLine($"Could not open {url}: {ex.Message}"); }
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = path, UseShellExecute = true });
            else
                MessageBox.Show($"Not found:\n{path}", "Pinscreen Library",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { Console.Error.WriteLine($"Could not open {path}: {ex.Message}"); }
    }

    private void Restart()
    {
        // Exiting is enough: the watchdog relaunches within ~10s.
        Console.WriteLine("Tray: restart requested");
        Environment.Exit(0);
    }

    private void Stop()
    {
        if (MessageBox.Show(
                "Stop the library server?\n\nThe pinscreens will not be able to sync until it starts again " +
                "(automatically at your next sign-in, or from the Start Menu shortcut).",
                "Pinscreen Library", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        // The watchdog relaunches on exit, so a plain exit cannot stop anything.
        // Drop the sentinel it checks after each run.
        try { File.WriteAllText(Path.Combine(_exeDir, "stop.flag"), "stopped from tray"); }
        catch (Exception ex) { Console.Error.WriteLine($"Could not write stop.flag: {ex.Message}"); }
        Console.WriteLine("Tray: stop requested");
        Environment.Exit(0);
    }

    public void Dispose()
    {
        try { _tooltipTimer?.Stop(); } catch { }
        try { if (_icon != null) { _icon.Visible = false; _icon.Dispose(); } } catch { }
        try { _ctx?.ExitThread(); } catch { }
    }
}
