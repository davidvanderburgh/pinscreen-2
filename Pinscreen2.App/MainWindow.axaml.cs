using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Avalonia.Interactivity;

namespace Pinscreen2.App;

public partial class MainWindow : Window
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private readonly DispatcherTimer _clockTimer = new DispatcherTimer();
    private readonly Queue<string> _playlist = new Queue<string>();
    private string _currentItem = string.Empty;
    private AppConfig _config = new AppConfig();
    // Expose config for overlay window
    public AppConfig Config => _config;

    // Public wrappers for overlay window actions
    public void PlayPauseCommand() => OnPlayPauseClicked(this, new Avalonia.Interactivity.RoutedEventArgs());
    public void NextCommand() => OnNextClicked(this, new Avalonia.Interactivity.RoutedEventArgs());
    public void RebuildQueueCommand() => OnRebuildQueueClicked(this, new Avalonia.Interactivity.RoutedEventArgs());
    public void OpenConfigCommand() => OnOpenConfigClicked(this, new Avalonia.Interactivity.RoutedEventArgs());
    public void OpenCurrentFolderCommand() => OnOpenCurrentFolderClicked(this, new Avalonia.Interactivity.RoutedEventArgs());
    public void SetMediaFolderCommand() => OnSetMediaFolderClicked(this, new Avalonia.Interactivity.RoutedEventArgs());
    public void QuitCommand() => OnQuitClicked(this, new Avalonia.Interactivity.RoutedEventArgs());
    //

    public MainWindow()
    {
        InitializeComponent();
        InitializeAsync();
        this.KeyDown += OnKeyDown;
        // Ensure we receive click/tap anywhere (even if child controls mark handled)
        AddHandler(InputElement.PointerPressedEvent, OnRootPointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        // No special positioning needed; clock is a centered popup
    }

    private async void InitializeAsync()
    {
        var libVlcPath = GetLibVlcDirectory();
        if (!string.IsNullOrWhiteSpace(libVlcPath))
        {
            SetPlatformLibraryEnv(libVlcPath);
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

        // Initialize LibVLC with software decode; let VideoView callbacks choose vout
        _libVlc = new LibVLC(new[] { "--vout=opengl", "--avcodec-hw=none", "--no-video-title-show" });
        _mediaPlayer = new MediaPlayer(_libVlc);
        _mediaPlayer.EncounteredError += (_, __) => Dispatcher.UIThread.Post(PlayNext);

        // Disable VLC marquee to avoid conflicting clock overlays
        try { _mediaPlayer.SetMarqueeInt(VideoMarqueeOption.Enable, 0); } catch { }

        // Defer attaching the MediaPlayer until the VideoView is attached and sized
        VideoView.AttachedToVisualTree += (_, __) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_mediaPlayer == null) return;
                if (VideoView.MediaPlayer == null && VideoView.Bounds.Width > 0 && VideoView.Bounds.Height > 0)
                {
                    VideoView.MediaPlayer = _mediaPlayer;
                    _mediaPlayer.EndReached += (_, __) => Dispatcher.UIThread.Post(PlayNext);
                    // Ensure overlay is hidden so VideoView is visible
                    try { if (OverlayPopup != null) OverlayPopup.IsOpen = false; } catch { }
                    PlayNext();
                }
            }, DispatcherPriority.Render);
        };

        // If already visible, try immediate attach
        if (_mediaPlayer != null && VideoView.IsEffectivelyVisible && VideoView.MediaPlayer == null)
        {
            VideoView.MediaPlayer = _mediaPlayer;
            _mediaPlayer.EndReached += (_, __) => Dispatcher.UIThread.Post(PlayNext);
            try { if (OverlayPopup != null) OverlayPopup.IsOpen = false; } catch { }
            PlayNext();
        }

        // Removed external OverlayWindow to keep overlay contained within the app window
    }

    private void ToggleOverlay(bool? force = null)
    {
        try
        {
            if (OverlayPopup == null) return;
            var newState = force ?? !OverlayPopup.IsOpen;
            OverlayPopup.IsOpen = newState;
        }
        catch { }
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only open overlay when closed; closing handled by backdrop/Escape
        e.Handled = true;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (OverlayPopup != null && !OverlayPopup.IsOpen)
                    ToggleOverlay(true);
            }
            catch { }
        }, DispatcherPriority.Background);
    }

    private void OnOverlayBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        // Clicking backdrop closes overlay
        ToggleOverlay(force: false);
        e.Handled = true;
    }

    private void OnOverlayPanelPressed(object? sender, PointerPressedEventArgs e)
    {
        // Prevent backdrop handler from closing when interacting inside panel
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ToggleOverlay(force: false);
        }
    }

    private void OnPlayPauseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_mediaPlayer == null) return;
        if (_mediaPlayer.IsPlaying)
            _mediaPlayer.Pause();
        else
            _mediaPlayer.Play();
    }

    private void OnNextClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        PlayNext();
    }

    private void OnRebuildQueueClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _ = BuildPlaylistAsync();
    }

    private void OnOpenConfigClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(path))
            {
                OpenWithOS(path);
            }
        }
        catch { }
    }

    private void OnOpenCurrentFolderClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(_currentItem))
            {
                var folder = Path.GetDirectoryName(_currentItem);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    OpenWithOS(folder);
                }
            }
        }
        catch { }
    }

    private async void OnSetMediaFolderClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Media Folder"
            };
            var selected = await dialog.ShowAsync(this);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                // Update config with single folder for simplicity; extend to multi-select later if needed
                _config.MediaFolders = new List<string> { selected };
                SaveConfig();
                await BuildPlaylistAsync();
                ToggleOverlay(false);
                // Auto-play immediately
                PlayNext();
            }
        }
        catch { }
    }

    private void SaveConfig()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }

    private static void OpenWithOS(string path)
    {
        if (OperatingSystem.IsMacOS())
            System.Diagnostics.Process.Start("open", $"\"{path}\"");
        else if (OperatingSystem.IsWindows())
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer", $"\"{path}\"") { UseShellExecute = true });
        else
            System.Diagnostics.Process.Start("xdg-open", $"\"{path}\"");
    }

    private void OnQuitClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
    private static void SetPlatformLibraryEnv(string libVlcDirectory)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // Ensure the dynamic loader can resolve libvlc*.dylib
                var existing = Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH") ?? string.Empty;
                var combined = string.IsNullOrEmpty(existing)
                    ? libVlcDirectory
                    : libVlcDirectory + System.IO.Path.PathSeparator + existing;
                Environment.SetEnvironmentVariable("DYLD_LIBRARY_PATH", combined);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
                var combined = string.IsNullOrEmpty(existing)
                    ? libVlcDirectory
                    : libVlcDirectory + System.IO.Path.PathSeparator + existing;
                Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", combined);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Prepend to PATH so libvlc.dll can be found
                var existing = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                var combined = string.IsNullOrEmpty(existing)
                    ? libVlcDirectory
                    : libVlcDirectory + ";" + existing;
                Environment.SetEnvironmentVariable("PATH", combined);
            }
        }
        catch { /* best effort */ }
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
        var now = DateTime.Now.ToString(_config.ClockFormat);
        try
        {
            if (ClockText != null)
            {
                ClockText.Text = now;
                return;
            }
        }
        catch { }
        try
        {
            var clock = this.FindControl<TextBlock>("ClockText");
            if (clock != null) clock.Text = now;
        }
        catch { }
    }

    private async Task BuildPlaylistAsync()
    {
        var folders = _config.MediaFolders.ToList();
        var collected = new List<string>();
        int totalFound = 0;

        await Task.Run(() =>
        {
            foreach (var folder in folders)
            {
                var resolved = ResolveFolderPath(folder);
                if (string.IsNullOrWhiteSpace(resolved) || !Directory.Exists(resolved))
                {
                    Console.WriteLine($"Scan skip: folder not found -> '{folder}' (resolved='{resolved}')");
                    continue;
                }
                Console.WriteLine($"Scanning: {resolved}");

                foreach (var file in EnumerateVideoFilesSafe(resolved))
                {
                    collected.Add(file);
                    totalFound++;
                }
            }
        });

        IEnumerable<string> finalOrder = collected;
        if (_config.BalanceQueueByGame)
        {
            // Simple grouping by immediate parent folder name
            var groups = collected.GroupBy(p => new DirectoryInfo(Path.GetDirectoryName(p) ?? string.Empty).Name)
                                  .Select(g => new Queue<string>(g))
                                  .ToList();
            var interleaved = new List<string>();
            bool added;
            do
            {
                added = false;
                foreach (var g in groups)
                {
                    if (g.Count > 0)
                    {
                        interleaved.Add(g.Dequeue());
                        added = true;
                    }
                }
            } while (added);
            finalOrder = interleaved;
        }

        _playlist.Clear();
        foreach (var f in finalOrder)
            _playlist.Enqueue(f);

        Console.WriteLine($"Scan complete: {_playlist.Count} files queued (found {totalFound})");
        UpdateStatus();
    }

    private static IEnumerable<string> EnumerateVideoFilesSafe(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> subdirs = Array.Empty<string>();
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
            }
            catch { }
            foreach (var sd in subdirs)
            {
                stack.Push(sd);
            }

            IEnumerable<string> files = Array.Empty<string>();
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch { }
            foreach (var f in files)
            {
                if (HasVideoExtension(f))
                    yield return f;
            }
        }
    }

    private static string ResolveFolderPath(string configuredPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configuredPath)) return string.Empty;

            // Expand '~' (macOS/Linux)
            var expanded = configuredPath.StartsWith("~")
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), configuredPath.TrimStart('~').TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : configuredPath;

            // Absolute path as-is
            if (Path.IsPathRooted(expanded) && Directory.Exists(expanded)) return expanded;

            // Relative to current working directory
            var cwdCandidate = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, expanded));
            if (Directory.Exists(cwdCandidate)) return cwdCandidate;

            // Relative to app base directory (where config.json is copied)
            var baseCandidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, expanded));
            if (Directory.Exists(baseCandidate)) return baseCandidate;
        }
        catch { }

        return configuredPath;
    }

    private static bool HasVideoExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".mov" or ".m4v" or ".mkv" or ".avi" or ".webm";
    }

    private void PlayNext()
    {
        if (_mediaPlayer == null || _libVlc == null) return;

        if (_playlist.Count == 0)
        {
            _ = BuildPlaylistAsync().ContinueWith(_ =>
            {
                if (_playlist.Count > 0)
                    Dispatcher.UIThread.Post(PlayNext);
                else
                    Dispatcher.UIThread.Post(UpdateStatus);
            });
            return;
        }

        _currentItem = _playlist.Dequeue();
        var media = new Media(_libVlc, _currentItem, FromType.FromPath);
        _mediaPlayer.Play(media);

        UpdateStatus();
    }

    // No VLC marquee positioning; Avalonia clock overlay only

    private void UpdateStatus()
    {
        try
        {
            var status = this.FindControl<TextBlock>("StatusText");
            if (status == null) return;
            var firstFolder = _config.MediaFolders.FirstOrDefault() ?? "(none)";
            status.Text = $"Folder: {firstFolder}   Queue: {_playlist.Count}   Now: {Path.GetFileName(_currentItem)}";
        }
        catch { }
    }
}

public class AppConfig
{
    public List<string> MediaFolders { get; set; } = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
    public string ClockFormat { get; set; } = "HH:mm:ss";
    public bool BalanceQueueByGame { get; set; } = true;
    public string LibVlcPath { get; set; } = string.Empty;
}