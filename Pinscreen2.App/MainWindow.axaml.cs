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
using System.Net.Http;
using System.Reflection;
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
    private bool _isInitializingUi = true;
    private string _effectiveConfigPath = string.Empty;
    private const string GitHubUpdateRepo = "davidvanderburgh/pinscreen-2"; // permanently linked repo
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
        _isInitializingUi = false; // prevent UI event handlers from saving during initial layout
        SetupClock();
        this.AttachedToVisualTree += (_, __) =>
        {
            try { UpdateClock(); } catch { }
            try { UpdateVersionInfo(); } catch { }
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
                try { UpdateVersionInfo(); } catch { }
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
            var clock24hCheck = this.FindControl<CheckBox>("Clock24hCheck");
            var sizeSlider = this.FindControl<Slider>("ClockSizeSlider");
            var sizeValueText = this.FindControl<TextBlock>("ClockSizeValueText");
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
            if (clock24hCheck != null)
            {
                var is24 = (_config.ClockFormat ?? "HH:mm:ss").Contains('H');
                clock24hCheck.IsChecked = is24;
            }
            if (sizeSlider != null)
            {
                sizeSlider.Value = Math.Clamp(_config.ClockFontSize, 24, 200);
                if (sizeValueText != null)
                    sizeValueText.Text = $"{(int)sizeSlider.Value}px";
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
        else if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
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
            var path = GetUserConfigFilePath();
            // Ensure file exists so the OS open command works
            if (!File.Exists(path)) SaveConfig();
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
            var path = GetUserConfigFilePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            _effectiveConfigPath = path;
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

    private async void OnInstallUpdateZipClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            // Pick a zip file
            var files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Select Update Zip",
                FileTypeFilter = new[] { new FilePickerFileType("Zip") { Patterns = new[] { "*.zip" }.ToList() } }
            });
            var file = files?.FirstOrDefault();
            var zipPath = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(zipPath)) return;

            var appDir = AppContext.BaseDirectory;
            var exeName = OperatingSystem.IsWindows() ? "Pinscreen2.App.exe" : (OperatingSystem.IsMacOS() ? "Pinscreen2.App" : "Pinscreen2.App");
            var updaterPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Pinscreen2.Updater", "bin", "Debug", "net9.0", OperatingSystem.IsWindows() ? "Pinscreen2.Updater.exe" : "Pinscreen2.Updater");
            // If not found in dev path, try alongside app (for published scenarios)
            if (!File.Exists(updaterPath))
            {
                updaterPath = Path.Combine(appDir, OperatingSystem.IsWindows() ? "Pinscreen2.Updater.exe" : "Pinscreen2.Updater");
            }
            if (!File.Exists(updaterPath))
            {
                await ShowMessageAsync("Updater not found. Ensure Pinscreen2.Updater is deployed next to the app or run from dev output.");
                return;
            }

            // Launch updater and quit
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = updaterPath,
                    UseShellExecute = true,
                    WorkingDirectory = appDir,
                    ArgumentList = { appDir, zipPath!, exeName }
                };
                try { psi.ArgumentList.Add(System.Diagnostics.Process.GetCurrentProcess().Id.ToString()); } catch { }
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync($"Failed to launch updater: {ex.Message}");
                return;
            }
            Close();
        }
        catch { }
    }

    private async void OnCheckUpdatesClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var apiLatest = $"https://api.github.com/repos/{GitHubUpdateRepo}/releases/latest";
            var apiList = $"https://api.github.com/repos/{GitHubUpdateRepo}/releases";
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Pinscreen2-Updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            http.Timeout = TimeSpan.FromSeconds(20);
            // Allow private repo via token
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
            {
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            JsonElement releaseEl = default;
            try
            {
                using var resp = await http.GetAsync(apiLatest);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // No latest release (or private repo without token). Try list and pick first non-draft
                    using var respList = await http.GetAsync(apiList);
                    if (!respList.IsSuccessStatusCode)
                    {
                        await ShowMessageAsync($"Update check failed: {(int)respList.StatusCode} {respList.ReasonPhrase}. Ensure a published release exists and API access.");
                        return;
                    }
                    var arr = JsonDocument.Parse(await respList.Content.ReadAsByteArrayAsync()).RootElement;
                    if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                    {
                        await ShowMessageAsync("No releases found. Publish a release on GitHub.");
                        return;
                    }
                    // Prefer first non-draft; include prereleases
                    var pick = arr.EnumerateArray().FirstOrDefault(e => e.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.False);
                    if (pick.ValueKind == JsonValueKind.Undefined)
                        pick = arr.EnumerateArray().First();
                    releaseEl = pick;
                }
                else if (resp.IsSuccessStatusCode)
                {
                    releaseEl = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync()).RootElement;
                }
                else
                {
                    await ShowMessageAsync($"Update check failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. If the repo is private, set GITHUB_TOKEN.");
                    return;
                }
            }
            catch (Exception ex)
            {
                await ShowMessageAsync($"Update check failed: {ex.Message}");
                return;
            }

            var tag = releaseEl.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? string.Empty : string.Empty;
            var assets = releaseEl.TryGetProperty("assets", out var assetsProp) ? assetsProp : default;

            var localVersion = GetLocalVersion();
            var remoteVersion = ParseVersion(tag);
            if (remoteVersion != null && localVersion != null && remoteVersion <= localVersion)
            {
                await ShowMessageAsync($"You're up to date. Current: {localVersion} Latest: {remoteVersion}");
                return;
            }

            // Find asset matching current platform
            string? downloadUrl = null;
            string? assetName = null;
            if (assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                    if (IsAssetMatchForCurrentRuntime(name))
                    {
                        downloadUrl = url;
                        assetName = name;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                await ShowMessageAsync("No suitable release asset found for this OS/architecture.");
                return;
            }

            // Download to temp
            string tmpFile;
            try
            {
                tmpFile = Path.Combine(Path.GetTempPath(), assetName ?? ("pinscreen2-update-" + Guid.NewGuid().ToString("N") + ".zip"));
                var data = await http.GetByteArrayAsync(downloadUrl);
                await File.WriteAllBytesAsync(tmpFile, data);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync($"Download failed: {ex.Message}");
                return;
            }

            // Launch updater
            var appDir = AppContext.BaseDirectory;
            var exeName = OperatingSystem.IsWindows() ? "Pinscreen2.App.exe" : (OperatingSystem.IsMacOS() ? "Pinscreen2.App" : "Pinscreen2.App");
            var updaterPath = Path.Combine(appDir, OperatingSystem.IsWindows() ? "Pinscreen2.Updater.exe" : "Pinscreen2.Updater");
            if (!File.Exists(updaterPath))
            {
                // Try dev output (running from IDE)
                var dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Pinscreen2.Updater", "bin", "Debug", "net9.0", OperatingSystem.IsWindows() ? "Pinscreen2.Updater.exe" : "Pinscreen2.Updater"));
                if (File.Exists(dev)) updaterPath = dev;
            }
            if (!File.Exists(updaterPath))
            {
                await ShowMessageAsync("Updater not found beside app. Ensure Pinscreen2.Updater is deployed.");
                return;
            }
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = updaterPath,
                    UseShellExecute = true,
                    WorkingDirectory = appDir,
                };
                psi.ArgumentList.Add(appDir);
                psi.ArgumentList.Add(tmpFile);
                psi.ArgumentList.Add(exeName);
                try { psi.ArgumentList.Add(System.Diagnostics.Process.GetCurrentProcess().Id.ToString()); } catch { }
                System.Diagnostics.Process.Start(psi);
                Close();
            }
            catch (Exception ex)
            {
                await ShowMessageAsync($"Failed to launch updater: {ex.Message}");
            }
        }
        catch { }
    }

    private static Version? GetLocalVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return ParseVersion(info);
            return asm.GetName().Version;
        }
        catch { return null; }
    }

    private static Version? ParseVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var s = v.Trim().TrimStart('v', 'V');
        Version ver;
        return Version.TryParse(s, out ver) ? ver : null;
    }

    private static bool IsAssetMatchForCurrentRuntime(string assetName)
    {
        var name = assetName.ToLowerInvariant();
        if (!name.EndsWith(".zip")) return false;
        if (OperatingSystem.IsWindows())
        {
            return name.Contains("win-x64") || name.Contains("windows") || name.Contains("win64") || name.Contains("win");
        }
        if (OperatingSystem.IsMacOS())
        {
            var isArm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            return (isArm && (name.Contains("osx-arm64") || name.Contains("mac-arm64") || name.Contains("macos-arm64")))
                || (!isArm && (name.Contains("osx-x64") || name.Contains("mac-x64") || name.Contains("macos-x64") || name.Contains("osx")));
        }
        // Linux
        return name.Contains("linux-x64") || name.Contains("linux");
    }

    private async Task ShowMessageAsync(string text)
    {
        try
        {
            // Suppress overlay auto-open while dialog is visible
            var prevSuppress = _suppressOverlayOpen;
            _suppressOverlayOpen = true;
            try { if (OverlayPopup != null) OverlayPopup.IsOpen = false; } catch { }

            var dlg = new Window
            {
                Width = 520,
                Height = 180,
                Title = "Pinscreen 2",
                CanResize = false,
                Topmost = true,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                Background = Brushes.Black,
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Children =
                    {
                        new TextBlock{ Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, MaxWidth = 460 },
                        new Button{ Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0,12,0,0) }
                    }
                }
            };
            if (dlg.Content is StackPanel sp && sp.Children.OfType<Button>().FirstOrDefault() is Button ok)
            {
                ok.Click += (_, __) => dlg.Close();
            }
            await dlg.ShowDialog(this);

            _suppressOverlayOpen = prevSuppress;
        }
        catch { _suppressOverlayOpen = false; }
    }

    private void OnToggleFullscreenClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        try
        {
            if (WindowState == WindowState.FullScreen)
                WindowState = WindowState.Normal;
            else
                WindowState = WindowState.FullScreen;
        }
        catch { }
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
            var path = GetEffectiveConfigFilePath();
            _effectiveConfigPath = path;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                try
                {
                    var readOptions = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };
                    _config = JsonSerializer.Deserialize<AppConfig>(json, readOptions) ?? new AppConfig();
                }
                catch
                {
                    // Keep defaults if parse fails; do not overwrite user's file here
                    _config = new AppConfig();
                }
                // Do not modify or save here; respect user's file exactly as-is
            }
            else
            {
                _config = new AppConfig();
                // First run: create user config with defaults
                SaveConfig();
                _effectiveConfigPath = GetUserConfigFilePath();
            }
        }
        catch { /* fallback to defaults */ }
    }

    private static void NormalizeConfigForCurrentOS(AppConfig config)
    {
        // Avoid mutating user-provided values on load. Only apply minimal safety defaults
        // for missing fields without overriding provided ones.
        try
        {
            if (config.MediaFolders == null)
            {
                config.MediaFolders = new List<string>();
            }
            if (config.MediaFolders.Count == 0)
            {
                var fallback = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                if (!string.IsNullOrWhiteSpace(fallback))
                    config.MediaFolders.Add(fallback);
            }
            if (string.IsNullOrWhiteSpace(config.ClockFormat))
                config.ClockFormat = "HH:mm:ss";
            if (string.IsNullOrWhiteSpace(config.ClockColor))
                config.ClockColor = "#FFFFFFFF";
            if (config.ClockFontSize <= 0)
                config.ClockFontSize = 72.0;
            if (config.DelaySeconds < 0)
                config.DelaySeconds = 0;
            // Do not clear LibVlcPath automatically; respect user value even if path missing at load
        }
        catch { }
    }

    private static string GetLibVlcDirectory()
    {
        try
        {
            // If user configured a path, prefer it
            // Note: Cannot access instance field here; rely on config file directly
            var configPathUser = GetUserConfigFilePath();
            var configPathApp = Path.Combine(AppContext.BaseDirectory, "config.json");
            var tryPaths = new[] { configPathUser, configPathApp };
            var configPath = tryPaths.FirstOrDefault(File.Exists);
            if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
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

    private static string GetConfigDirectory()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pinscreen2");
                return dir;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // On macOS, ApplicationData resolves to ~/Library/Application Support
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pinscreen2");
                return dir;
            }
            else
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pinscreen2");
                return dir;
            }
        }
        catch { }
        return AppContext.BaseDirectory;
    }

    private static string GetUserConfigFilePath()
    {
        return Path.Combine(GetConfigDirectory(), "config.json");
    }

    private static string GetEffectiveConfigFilePath()
    {
        var user = GetUserConfigFilePath();
        if (File.Exists(user)) return user;
        var app = Path.Combine(AppContext.BaseDirectory, "config.json");
        return File.Exists(app) ? app : user;
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
                // Apply font size
                try { ClockText.FontSize = Math.Clamp(_config.ClockFontSize, 24, 200); } catch { }
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
            if (_isInitializingUi) return;
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
            if (_isInitializingUi) return;
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

    private void OnClockFormatChecked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_isInitializingUi) return;
            _config.ClockFormat = "HH:mm:ss"; // 24h
            SaveConfig();
            UpdateClock();
        }
        catch { }
    }

    private void OnClockFormatUnchecked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_isInitializingUi) return;
            _config.ClockFormat = "hh:mm:ss tt"; // 12h with AM/PM
            SaveConfig();
            UpdateClock();
        }
        catch { }
    }

    private void OnDelayChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        try
        {
            if (_isInitializingUi) return;
            _delaySeconds = (int)Math.Round(Math.Clamp(e.NewValue, 0, 10));
            _config.DelaySeconds = _delaySeconds;
            SaveConfig();
            var delayValueText = this.FindControl<TextBlock>("DelayValueText");
            if (delayValueText != null)
                delayValueText.Text = $"{_delaySeconds}s";
        }
        catch { }
    }

    private void OnClockSizeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        try
        {
            if (_isInitializingUi) return;
            var newSize = Math.Clamp(e.NewValue, 24, 200);
            _config.ClockFontSize = newSize;
            SaveConfig();
            if (ClockText != null)
            {
                try { ClockText.FontSize = newSize; } catch { }
                UpdateClock();
            }
            var sizeValueText = this.FindControl<TextBlock>("ClockSizeValueText");
            if (sizeValueText != null)
                sizeValueText.Text = $"{(int)newSize}px";
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
            var cfgSrc = (!string.IsNullOrWhiteSpace(_effectiveConfigPath) && _effectiveConfigPath.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)) ? "cfg:app" : "cfg:user";
            status.Text = $"Folder: {firstFolder}   Queue: {_playlist.Count}   Now: {Path.GetFileName(_currentItem)}   {vlcStatus}   {cfgSrc}";
        }
        catch { }
    }

    private void UpdateVersionInfo()
    {
        try
        {
            var versionText = this.FindControl<TextBlock>("VersionText");
            if (versionText == null) return;
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var ver = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? asm.GetName().Version?.ToString() ?? "0.0.0";
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            string updated = "";
            try
            {
                var fi = !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath) ? new FileInfo(exePath) : null;
                if (fi != null)
                {
                    updated = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                }
            }
            catch { }
            versionText.Text = string.IsNullOrWhiteSpace(updated) ? $"Version: {ver}" : $"Version: {ver}   Updated: {updated}";
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
    public double ClockFontSize { get; set; } = 72.0;
    // UpdateGitHubRepo no longer needed; updater is permanently linked to the repo in code
}