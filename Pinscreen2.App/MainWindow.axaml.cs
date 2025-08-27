using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Pinscreen2.App;

public partial class MainWindow : Window
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private readonly DispatcherTimer _clockTimer = new DispatcherTimer();
    private readonly Queue<string> _playlist = new Queue<string>();
    private string _currentItem = string.Empty;
    private AppConfig _config = new AppConfig();
    private double _clockXPercent = 50.0; // 0-100
    private double _clockYPercent = 50.0; // 0-100
    private bool _isDraggingClock = false;
    private bool _suppressOverlayOpen = false;
    private Avalonia.Point _dragStartPointer;
    private Avalonia.Point _dragStartClockPos;
    private const double ClockEdgePadding = 8.0;
    private string _clockColorHex = "#FFFFFFFF";
    private int _delaySeconds = 3;
    private bool _libVlcInitFailed = false;
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
            RoutingStrategies.Bubble, handledEventsToo: false);
        // No special positioning needed; clock is a centered popup
        try { if (OverlayPopup != null) OverlayPopup.Opened += (_, __) => TryPopulateClockFontCombo(); } catch { }
    }

    private async void InitializeAsync()
    {
        var libVlcPath = GetLibVlcDirectory();
        try
        {
            if (!string.IsNullOrWhiteSpace(libVlcPath))
            {
                SetPlatformLibraryEnv(libVlcPath);
                SetVlcPluginPath(libVlcPath);
                Core.Initialize(libVlcPath);
                try { Console.WriteLine($"LibVLC Core.Initialize using: {libVlcPath}"); } catch { }
            }
            else
            {
                Core.Initialize();
                try { Console.WriteLine("LibVLC Core.Initialize using default lookup (PATH/current dir)"); } catch { }
            }
        }
        catch (Exception ex)
        {
            _libVlcInitFailed = true;
            try { Console.WriteLine($"LibVLC initialization failed: {ex.Message}"); } catch { }
        }
        LoadConfig();
        // Initialize clock position from config if present
        _clockXPercent = Math.Clamp(_config.ClockXPercent, 0, 100);
        _clockYPercent = Math.Clamp(_config.ClockYPercent, 0, 100);
        _clockColorHex = string.IsNullOrWhiteSpace(_config.ClockColor) ? _clockColorHex : _config.ClockColor;
        _delaySeconds = Math.Clamp(_config.DelaySeconds, 0, 10);
        SetupClock();
        this.AttachedToVisualTree += (_, __) =>
        {
            try { UpdateClock(); } catch { }
        };
        await BuildPlaylistAsync();

        // Initialize LibVLC with software decode; let VideoView callbacks choose vout
        try
        {
            _libVlc = new LibVLC(new[] { "--vout=opengl", "--avcodec-hw=none", "--no-video-title-show" });
            _mediaPlayer = new MediaPlayer(_libVlc);
            _mediaPlayer.EncounteredError += (_, __) => Dispatcher.UIThread.Post(PlayNext);
            _libVlcInitFailed = false;
        }
        catch (Exception ex)
        {
            _libVlcInitFailed = true;
            try { Console.WriteLine($"LibVLC create failed: {ex.Message}"); } catch { }
        }

        // Disable VLC marquee to avoid conflicting clock overlays
        try { _mediaPlayer?.SetMarqueeInt(VideoMarqueeOption.Enable, 0); } catch { }

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
            if (newState)
            {
                TryPopulateClockFontCombo();
            }
        }
        catch { }
    }
    private void TryPopulateClockFontCombo()
    {
        try
        {
            var fontCombo = this.FindControl<ComboBox>("ClockFontCombo");
            var colorCombo = this.FindControl<ComboBox>("ClockColorCombo");
            var delaySlider = this.FindControl<Slider>("DelaySlider");
            var delayValueText = this.FindControl<TextBlock>("DelayValueText");
            if (fontCombo == null) return;
            var externalFontsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
            var items = new List<string>();
            if (Directory.Exists(externalFontsDir))
            {
                var files = Directory.EnumerateFiles(externalFontsDir)
                    .Where(p => p.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileName)
                    .Select(n => n ?? string.Empty)
                    .Where(n => n.Length > 0)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                items.AddRange(files);
            }
            fontCombo.ItemsSource = items;
            if (!string.IsNullOrWhiteSpace(_config.ClockFontFamily))
            {
                var match = items.FirstOrDefault(n => string.Equals(n, _config.ClockFontFamily, StringComparison.OrdinalIgnoreCase)
                                                     || string.Equals(Path.GetFileNameWithoutExtension(n), _config.ClockFontFamily, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    fontCombo.SelectedItem = match;
                }
            }
            if (colorCombo != null)
            {
                foreach (var it in colorCombo.Items.OfType<ComboBoxItem>())
                {
                    if (string.Equals(it.Tag?.ToString(), _clockColorHex, StringComparison.OrdinalIgnoreCase))
                    {
                        colorCombo.SelectedItem = it;
                        break;
                    }
                }
            }
            if (delaySlider != null)
            {
                delaySlider.Value = _delaySeconds;
                if (delayValueText != null)
                    delayValueText.Text = $"{_delaySeconds}s";
            }
        }
        catch { }
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Ignore while dragging the clock to prevent unintended overlay opens
        if (_isDraggingClock || _suppressOverlayOpen)
            return;

        // Ignore clicks originating from clock or overlay content
        try
        {
            if (e.Source is Control c)
            {
                // Walk up using Parent chain for Controls only
                var current = c;
                while (current != null)
                {
                    if (ReferenceEquals(current, ClockText) || current.Name == "OverlayPanel")
                        return;
                    current = current.Parent as Control;
                }
            }
        }
        catch { }

        // Only open overlay when closed; closing handled by backdrop/Escape
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_suppressOverlayOpen || _isDraggingClock)
                    return;
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
            // Ensure overlay doesn't block interaction and window isn't top-most
            bool wasTopmost = false;
            bool wasOverlayOpen = false;
            bool wasClockOpen = false;
            try
            {
                _suppressOverlayOpen = true; // block overlay toggle while dialog is open
                wasTopmost = this.Topmost;
                wasOverlayOpen = OverlayPopup?.IsOpen == true;
                wasClockOpen = ClockPopup?.IsOpen == true;
                if (OverlayPopup != null && OverlayPopup.IsOpen) OverlayPopup.IsOpen = false;
                if (ClockPopup != null && ClockPopup.IsOpen) ClockPopup.IsOpen = false;
                this.Topmost = false;
                this.Activate();
                await Task.Delay(50);
            }
            catch { }

            // Prefer native modal dialog for reliability on Windows
            string? selectedPath = null;
            try
            {
                var result = await this.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    AllowMultiple = false,
                    Title = "Select Media Folder"
                });
                var folder = result?.FirstOrDefault();
                selectedPath = folder?.TryGetLocalPath();
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                _config.MediaFolders = new List<string> { selectedPath! };
                SaveConfig();
                await BuildPlaylistAsync();
                ToggleOverlay(false);
                PlayNext();
            }
            // Restore original top-most state and overlay suppression
            try
            {
                this.Topmost = wasTopmost;
                if (ClockPopup != null)
                    ClockPopup.IsOpen = wasClockOpen; // restore clock visibility
                try { UpdateClock(); } catch { }
            }
            catch { }
            finally
            {
                _suppressOverlayOpen = false;
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
                NormalizeConfigForCurrentOS(_config);
            }
            else
            {
                _config = new AppConfig();
                NormalizeConfigForCurrentOS(_config);
            }
        }
        catch { /* fallback to defaults */ }
    }

    private static void NormalizeConfigForCurrentOS(AppConfig config)
    {
        try
        {
            // Media folders: remove obvious non-existent platform-specific defaults
            if (config.MediaFolders == null || config.MediaFolders.Count == 0)
            {
                config.MediaFolders = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
            }
            else
            {
                var normalized = new List<string>();
                foreach (var f in config.MediaFolders)
                {
                    if (string.IsNullOrWhiteSpace(f)) continue;
                    var resolved = ResolveFolderPath(f);
                    if (Directory.Exists(resolved))
                    {
                        normalized.Add(resolved);
                    }
                }
                if (normalized.Count == 0)
                {
                    normalized.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
                }
                config.MediaFolders = normalized.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            // LibVLC path: if configured but not valid on this OS, clear it
            if (!string.IsNullOrWhiteSpace(config.LibVlcPath))
            {
                try
                {
                    var libName = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libvlc.dylib"
                        : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "libvlc.dll"
                        : "libvlc.so";
                    var lib = Path.Combine(config.LibVlcPath, libName);
                    if (!Directory.Exists(config.LibVlcPath) || !File.Exists(lib))
                    {
                        config.LibVlcPath = string.Empty;
                    }
                }
                catch { config.LibVlcPath = string.Empty; }
            }
        }
        catch { }
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
                // Common Windows locations
                var candidates = new List<string>();
                try
                {
                    var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                    if (!string.IsNullOrEmpty(programFiles))
                        candidates.Add(Path.Combine(programFiles, "VideoLAN", "VLC"));
                    if (!string.IsNullOrEmpty(programFilesX86))
                        candidates.Add(Path.Combine(programFilesX86, "VideoLAN", "VLC"));
                }
                catch { }

                // Also scan PATH for libvlc.dll
                try
                {
                    var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    var parts = pathEnv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var p in parts)
                    {
                        var dir = p.Trim();
                        if (string.IsNullOrWhiteSpace(dir)) continue;
                        candidates.Add(dir);
                    }
                }
                catch { }

                foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var dll = Path.Combine(c, "libvlc.dll");
                        if (File.Exists(dll))
                            return c;
                    }
                    catch { }
                }
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
                if (!string.IsNullOrWhiteSpace(_config.ClockFontFamily))
                {
                    try { ApplyClockFontSafely(_config.ClockFontFamily); } catch { }
                }
                // Apply color
                try { ClockText.Foreground = new SolidColorBrush(Color.Parse(_clockColorHex)); } catch { }
                ClockText.Text = now;
                // Position clock based on percentage within the RootGrid
                try
                {
                    var root = this.FindControl<Grid>("RootGrid");
                    if (root != null)
                    {
                        // Measure the text to get accurate size for clamping
                        ClockText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        var textSize = ClockText.DesiredSize;
                        var width = Math.Max(0, root.Bounds.Width - (ClockEdgePadding * 2));
                        var height = Math.Max(0, root.Bounds.Height - (ClockEdgePadding * 2));
                        var maxLeft = Math.Max(0, width - textSize.Width);
                        var maxTop = Math.Max(0, height - textSize.Height);
                        var x = ClockEdgePadding + maxLeft * (_clockXPercent / 100.0);
                        var y = ClockEdgePadding + maxTop * (_clockYPercent / 100.0);
                        Canvas.SetLeft(ClockText, x);
                        Canvas.SetTop(ClockText, y);
                    }
                }
                catch { }
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

    private void OnClockFontSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (sender is ComboBox combo)
            {
                var fileName = combo.SelectedItem as string;
                var familyName = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
                var effective = !string.IsNullOrWhiteSpace(fileName) ? fileName : familyName;
                if (!string.IsNullOrWhiteSpace(effective))
                {
                    _config.ClockFontFamily = effective;
                    SaveConfig();
                    try { ApplyClockFontSafely(effective); } catch { }
                }
            }
        }
        catch { }
    }

    private void OnClockColorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (sender is ComboBox combo)
            {
                var item = combo.SelectedItem as ComboBoxItem;
                var hex = item?.Tag?.ToString();
                if (!string.IsNullOrWhiteSpace(hex))
                {
                    _clockColorHex = hex!;
                    _config.ClockColor = _clockColorHex;
                    SaveConfig();
                    try
                    {
                        if (ClockText != null)
                            ClockText.Foreground = new SolidColorBrush(Color.Parse(_clockColorHex));
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private void OnDelayChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        try
        {
            _delaySeconds = (int)Math.Round(Math.Clamp(e.NewValue, 0, 10));
            _config.DelaySeconds = _delaySeconds;
            SaveConfig();
            var delayValueText = this.FindControl<TextBlock>("DelayValueText");
            if (delayValueText != null)
                delayValueText.Text = $"{_delaySeconds}s";
        }
        catch { }
    }

    private void ApplyClockFontSafely(string fileOrFamily)
    {
        if (ClockText == null) return;
        // Build candidate font family URIs and validate by measuring a temporary TextBlock
        var candidates = new List<string>();
        var assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", fileOrFamily);
        if (File.Exists(assetsPath))
        {
            var baseName = Path.GetFileNameWithoutExtension(fileOrFamily);
            var spaced = baseName.Replace("_", " ").Replace("-", " ");
            var title = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(spaced.ToLowerInvariant());
            // Try several possible family name variants
            candidates.Add($"avares://Pinscreen2.App/Assets/Fonts/{fileOrFamily}#{baseName}");
            if (!string.Equals(title, baseName, StringComparison.Ordinal))
                candidates.Add($"avares://Pinscreen2.App/Assets/Fonts/{fileOrFamily}#{title}");
            if (!string.Equals(spaced, baseName, StringComparison.Ordinal))
                candidates.Add($"avares://Pinscreen2.App/Assets/Fonts/{fileOrFamily}#{spaced}");
            // Also try without family suffix as last resort
            candidates.Add($"avares://Pinscreen2.App/Assets/Fonts/{fileOrFamily}");
        }
        else
        {
            // Assume it's a system-installed family name
            candidates.Add(fileOrFamily);
        }

        foreach (var cand in candidates.Distinct())
        {
            try
            {
                var ff = new FontFamily(cand);
                var probe = new TextBlock { FontFamily = ff, Text = "Aa", FontSize = 14 };
                // This triggers glyph resolution within try/catch
                probe.Measure(new Size(100, 40));
                ClockText.FontFamily = ff;
                return;
            }
            catch { }
        }

        // Fallback to default if none worked
        try { ClockText.FontFamily = new FontFamily("Inter"); } catch { }
    }

    private void OnClockPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            if (ClockText == null) return;
            var parent = ClockText.Parent as Control;
            var pos = e.GetPosition(parent);
            _dragStartPointer = pos;
            _dragStartClockPos = new Avalonia.Point(Canvas.GetLeft(ClockText), Canvas.GetTop(ClockText));
            _isDraggingClock = true;
            _suppressOverlayOpen = true;
            e.Pointer.Capture(ClockText);
            e.Handled = true;
        }
        catch { }
    }

    private void OnClockPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingClock || ClockText == null) return;
        try
        {
            var parent = ClockText.Parent as Control;
            if (parent == null) return;
            var current = e.GetPosition(parent);
            var delta = current - _dragStartPointer;
            var newLeft = _dragStartClockPos.X + delta.X;
            var newTop = _dragStartClockPos.Y + delta.Y;
            // Measure current text size for precise clamping
            ClockText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var textSize = ClockText.DesiredSize;
            var maxLeft = Math.Max(0, parent.Bounds.Width - ClockEdgePadding - textSize.Width);
            var maxTop = Math.Max(0, parent.Bounds.Height - ClockEdgePadding - textSize.Height);
            newLeft = Math.Max(ClockEdgePadding, Math.Min(newLeft, maxLeft));
            newTop = Math.Max(ClockEdgePadding, Math.Min(newTop, maxTop));
            Canvas.SetLeft(ClockText, newLeft);
            Canvas.SetTop(ClockText, newTop);

            // Update percents live
            var usableWidth = Math.Max(0.0001, parent.Bounds.Width - (ClockEdgePadding * 2) - textSize.Width);
            var usableHeight = Math.Max(0.0001, parent.Bounds.Height - (ClockEdgePadding * 2) - textSize.Height);
            _clockXPercent = ((newLeft - ClockEdgePadding) / usableWidth) * 100.0;
            _clockYPercent = ((newTop - ClockEdgePadding) / usableHeight) * 100.0;
            _config.ClockXPercent = _clockXPercent;
            _config.ClockYPercent = _clockYPercent;
            SaveConfig();
            e.Handled = true;
        }
        catch { }
    }

    private void OnClockPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
        {
            _isDraggingClock = false;
            e.Pointer.Capture(null);
            // Re-enable overlay opening on subsequent clicks
            _suppressOverlayOpen = false;
            e.Handled = true;
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

        // Randomize order each build
        var rng = new Random();
        IEnumerable<string> finalOrder = collected;
        if (_config.BalanceQueueByGame)
        {
            // Group by immediate parent folder name
            var grouped = collected
                .GroupBy(p => new DirectoryInfo(Path.GetDirectoryName(p) ?? string.Empty).Name)
                // Shuffle items within each group
                .Select(g => g.OrderBy(_ => rng.Next()).ToList())
                .ToList();

            // Shuffle group order
            grouped = grouped.OrderBy(_ => rng.Next()).ToList();

            // Interleave one item from each group until all exhausted
            var queues = grouped.Select(g => new Queue<string>(g)).ToList();
            var interleaved = new List<string>();
            bool added;
            do
            {
                added = false;
                foreach (var q in queues)
                {
                    if (q.Count > 0)
                    {
                        interleaved.Add(q.Dequeue());
                        added = true;
                    }
                }
            } while (added);
            finalOrder = interleaved;
        }
        else
        {
            finalOrder = collected.OrderBy(_ => rng.Next()).ToList();
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
            try { _mediaPlayer?.Stop(); } catch { }
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
        // Apply delay if configured
        if (_delaySeconds > 0)
        {
            var delay = TimeSpan.FromSeconds(_delaySeconds);
            Dispatcher.UIThread.Post(async () =>
            {
                // Stop to ensure the screen returns to black during the delay
                try { _mediaPlayer?.Stop(); } catch { }
                try { await Task.Delay(delay); } catch { }
                if (_mediaPlayer == null || _libVlc == null) return;
                var mediaDelayed = new Media(_libVlc, _currentItem, FromType.FromPath);
                _mediaPlayer.Play(mediaDelayed);
            }, DispatcherPriority.Background);
        }
        else
        {
            var media = new Media(_libVlc, _currentItem, FromType.FromPath);
            _mediaPlayer.Play(media);
        }

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
            var vlcStatus = _libVlcInitFailed ? "VLC: missing" : (_libVlc == null ? "VLC: not ready" : "VLC: ok");
            status.Text = $"Folder: {firstFolder}   Queue: {_playlist.Count}   Now: {Path.GetFileName(_currentItem)}   {vlcStatus}";
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
    public double ClockXPercent { get; set; } = 50.0;
    public double ClockYPercent { get; set; } = 50.0;
    public string ClockFontFamily { get; set; } = string.Empty; // file path or family name
    public string ClockColor { get; set; } = "#FFFFFFFF";
    public int DelaySeconds { get; set; } = 3;
}