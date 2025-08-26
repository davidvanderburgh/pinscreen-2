using Avalonia.Controls;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace Pinscreen2.App;

public partial class MainWindow : Window
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private readonly DispatcherTimer _clockTimer = new DispatcherTimer();
    private readonly Queue<string> _playlist = new Queue<string>();
    private string _currentItem = string.Empty;
    private AppConfig _config = new AppConfig();

    public MainWindow()
    {
        InitializeComponent();
        InitializeAsync();
    }

    private async void InitializeAsync()
    {
        var libVlcPath = GetLibVlcDirectory();
        if (!string.IsNullOrWhiteSpace(libVlcPath))
        {
            SetVlcPluginPath(libVlcPath);
            Core.Initialize(libVlcPath);
        }
        else
        {
            Core.Initialize();
        }
        LoadConfig();
        SetupClock();
        await BuildPlaylistAsync();

        // Try with explicit plugin path option when available
        var options = new List<string> { "--verbose=2" };

        _libVlc = new LibVLC(options.ToArray());
        _libVlc.Log += (_, e) => Console.WriteLine($"libvlc[{e.Level}]: {e.Message}");
        _mediaPlayer = new MediaPlayer(_libVlc);
        VideoView.MediaPlayer = _mediaPlayer;
        _mediaPlayer.EndReached += (_, __) => Dispatcher.UIThread.Post(PlayNext);

        PlayNext();
    }

    private static void SetVlcPluginPath(string libVlcDirectory)
    {
        try
        {
            string? candidate = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                candidate = Path.GetFullPath(Path.Combine(libVlcDirectory, "..", "plugins"));
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                candidate = Path.Combine(libVlcDirectory, "plugins");
            }
            else
            {
                // Common linux locations relative to lib directory
                var rel = Path.Combine(libVlcDirectory, "vlc", "plugins");
                if (Directory.Exists(rel)) candidate = rel;
                if (string.IsNullOrEmpty(candidate))
                {
                    var common = new[] { "/usr/lib/vlc/plugins", "/usr/local/lib/vlc/plugins" };
                    candidate = common.FirstOrDefault(Directory.Exists) ?? string.Empty;
                }
            }

            if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate))
            {
                Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", candidate);
            }
        }
        catch { /* best effort */ }
    }

    private void LoadConfig()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                _config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch { /* fallback to defaults */ }
    }

    private static string GetLibVlcDirectory()
    {
        try
        {
            // If user configured a path, prefer it
            // Note: Cannot access instance field here; rely on config file directly
            var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (!string.IsNullOrWhiteSpace(cfg?.LibVlcPath) && Directory.Exists(cfg.LibVlcPath))
                {
                    // Basic sanity: check for libvlc presence
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    {
                        var lib = Path.Combine(cfg.LibVlcPath, "libvlc.dylib");
                        if (File.Exists(lib)) return cfg.LibVlcPath;
                    }
                    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        var lib = Path.Combine(cfg.LibVlcPath, "libvlc.dll");
                        if (File.Exists(lib)) return cfg.LibVlcPath;
                    }
                    else
                    {
                        var lib = Path.Combine(cfg.LibVlcPath, "libvlc.so");
                        if (File.Exists(lib)) return cfg.LibVlcPath;
                    }
                }
            }

            // OS defaults
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var macAppBundleLib = "/Applications/VLC.app/Contents/MacOS/lib";
                if (Directory.Exists(macAppBundleLib) && File.Exists(Path.Combine(macAppBundleLib, "libvlc.dylib")))
                    return macAppBundleLib;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var winDefault = Path.Combine(programFiles, "VideoLAN", "VLC");
                if (Directory.Exists(winDefault) && File.Exists(Path.Combine(winDefault, "libvlc.dll")))
                    return winDefault;
            }
            else
            {
                // Common Linux locations
                var candidates = new[]
                {
                    "/usr/lib/x86_64-linux-gnu/",
                    "/usr/lib/",
                    "/usr/local/lib/",
                };
                foreach (var c in candidates)
                {
                    if (Directory.Exists(c) && File.Exists(Path.Combine(c, "libvlc.so")))
                        return c;
                }
            }
        }
        catch { /* ignore and fall back */ }

        return string.Empty; // fall back to default resolution
    }

    private void SetupClock()
    {
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, __) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();
    }

    private void UpdateClock()
    {
        ClockText.Text = DateTime.Now.ToString(_config.ClockFormat);
    }

    private async Task BuildPlaylistAsync()
    {
        _playlist.Clear();
        foreach (var folder in _config.MediaFolders)
        {
            if (!Directory.Exists(folder)) continue;
            var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                                 .Where(f => HasVideoExtension(f));
            foreach (var file in files)
            {
                _playlist.Enqueue(file);
            }
        }

        if (_config.BalanceQueueByGame)
        {
            // Simple grouping by immediate parent folder name
            var groups = _playlist.GroupBy(p => new DirectoryInfo(Path.GetDirectoryName(p) ?? string.Empty).Name)
                                   .Select(g => new Queue<string>(g));
            _playlist.Clear();
            bool added;
            do
            {
                added = false;
                foreach (var g in groups)
                {
                    if (g.Count > 0)
                    {
                        _playlist.Enqueue(g.Dequeue());
                        added = true;
                    }
                }
            } while (added);
        }

        await Task.CompletedTask;
    }

    private static bool HasVideoExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".mov" or ".m4v" or ".mkv" or ".avi";
    }

    private void PlayNext()
    {
        if (_mediaPlayer == null || _libVlc == null) return;

        if (_playlist.Count == 0)
        {
            _ = BuildPlaylistAsync().ContinueWith(_ => Dispatcher.UIThread.Post(PlayNext));
            return;
        }

        _currentItem = _playlist.Dequeue();
        using var media = new Media(_libVlc, new Uri(_currentItem));
        _mediaPlayer.Media = media;
        _mediaPlayer.Play();
    }
}

public class AppConfig
{
    public List<string> MediaFolders { get; set; } = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
    public string ClockFormat { get; set; } = "HH:mm:ss";
    public bool BalanceQueueByGame { get; set; } = true;
    public string LibVlcPath { get; set; } = string.Empty;
}