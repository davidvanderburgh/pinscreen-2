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
using System.Threading;
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
    private RemoteLibraryClient? _remoteClient;
    private string _remoteStatus = string.Empty;
    private bool _isSyncing = false;
    private bool _isInitializingUi = true;
    private bool _needsInitialScan = false;
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
        this.Activated += OnWindowActivatedRestoreFullScreen;
        this.AttachedToVisualTree += (_, __) =>
        {
            try { UpdateClock(); } catch { }
            try { UpdateVersionInfo(); } catch { }
        };
        // Clock position is re-evaluated on the 1-second timer instead of from
        // Bounds/WindowState/ClientSize events. Those event sources fire in
        // bursts during monitor sleep/wake and were the root cause of past
        // hang and crash reports -- the timer-driven path is deterministic
        // and idempotent.

        // Playlist startup policy:
        //   * Cache present: load from cache (instant) and refresh in the
        //     background. App is up and playing immediately.
        //   * No cache: do NOT auto-scan. The first scan on a big library
        //     takes a long time and would silently delay the app from
        //     appearing. Instead let the window come up immediately and
        //     show a banner prompting the user to start the scan.
        if (HasPlaylistCache(_config.MediaFolders.Select(ResolveFolderPath).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).ToList()))
        {
            _ = BuildPlaylistAsync(useCache: true);
        }
        else
        {
            _needsInitialScan = true;
            // Auto-open the overlay once the window is up so the prompt is
            // visible without requiring the user to click first.
            Dispatcher.UIThread.Post(() =>
            {
                try { ToggleOverlay(true); } catch { }
                try { UpdateStatus(); } catch { }
            }, DispatcherPriority.Background);
        }

        // Initialize LibVLC. On Windows we default to the wingdi video output
        // module -- pure GDI software rendering with no D3D / DXVA calls. The
        // direct3d11 / direct3d9 / opengl vouts all eventually route through
        // the Intel HD Graphics user-mode drivers (igd10iumd64.dll,
        // igd11dxva64.dll, ...), and the 2016-era version shipped with the
        // cheap mini-PCs we target reliably AVs inside those DLLs. wingdi is
        // slower for high-res content but bulletproof, and the library here
        // is 1080p or smaller. Software decode (--avcodec-hw=none) covers
        // the same crash class on the decoder side. Override via config
        // (VlcVideoOutput) -- e.g. set "direct3d11" if you've updated the
        // graphics driver and want hardware-accelerated rendering back.
        try
        {
            var vlcArgs = new System.Collections.Generic.List<string> { "--avcodec-hw=none", "--no-video-title-show", "--quiet" };
            if (!string.IsNullOrWhiteSpace(_config.VlcVideoOutput))
                vlcArgs.Add($"--vout={_config.VlcVideoOutput}");
            else if (OperatingSystem.IsWindows())
                vlcArgs.Add("--vout=wingdi");
            Console.WriteLine($"LibVLC args: {string.Join(' ', vlcArgs)}");
            _libVlc = new LibVLC(vlcArgs.ToArray());
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

        // Attach MediaPlayer to VideoView only after the native HWND is ready
        // and Bounds are non-zero. If Play() runs before that, VLC opens its
        // own native sub-window. Drive the attach from VideoView.AttachedToVisualTree
        // and retry on Bounds changes until we get a real size. Once attached,
        // try to start playback (it'll wait if the playlist is still building).
        VideoView.AttachedToVisualTree += (_, __) =>
        {
            TryAttachMediaPlayer();
            // Bounds may not be set on the very first attach event on slow hardware.
            // Listen for BoundsProperty changes until we successfully attach.
            VideoView.PropertyChanged += (_, ev) =>
            {
                if (ev.Property == Visual.BoundsProperty && VideoView.MediaPlayer == null)
                    TryAttachMediaPlayer();
            };
        };
    }

    private void TryAttachMediaPlayer()
    {
        try
        {
            if (_mediaPlayer == null) return;
            if (VideoView.MediaPlayer != null) return;
            if (VideoView.Bounds.Width <= 0 || VideoView.Bounds.Height <= 0) return;

            VideoView.MediaPlayer = _mediaPlayer;
            _mediaPlayer.EndReached += (_, __) => Dispatcher.UIThread.Post(PlayNext);
            try { if (OverlayPopup != null) OverlayPopup.IsOpen = false; } catch { }
            TryStartPlayback();
        }
        catch (Exception ex)
        {
            try { Console.WriteLine($"TryAttachMediaPlayer failed: {ex.Message}"); } catch { }
        }
    }

    private void TryStartPlayback()
    {
        // Idempotent: only start if attached, not already playing, and the
        // playlist has at least one item. Called from both the VideoView
        // attach path and the BuildPlaylistAsync completion path so whichever
        // happens last kicks playback off.
        try
        {
            if (_mediaPlayer == null) return;
            if (VideoView.MediaPlayer == null) return;
            if (_mediaPlayer.IsPlaying) return;
            if (_playlist.Count == 0) return;
            PlayNext();
        }
        catch { }
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
                try { UpdateFirstRunBanner(); } catch { }
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
            var remoteUrlBox = this.FindControl<TextBox>("RemoteUrlBox");
            if (remoteUrlBox != null)
                remoteUrlBox.Text = _config.RemoteLibraryUrl ?? string.Empty;
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

    private async void OnScanLibraryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Same effect as Rebuild Queue but explicit about what it does and
        // wired to the first-run banner so the banner hides only after the
        // scan actually completes.
        try
        {
            await BuildPlaylistAsync();
            _needsInitialScan = false;
            UpdateFirstRunBanner();
            try { ToggleOverlay(false); } catch { }
            try { TryStartPlayback(); } catch { }
        }
        catch { }
    }

    private void UpdateFirstRunBanner()
    {
        try
        {
            var banner = this.FindControl<Border>("FirstRunBanner");
            if (banner != null) banner.IsVisible = _needsInitialScan;
        }
        catch { }
    }

    private async void OnApplyRemoteUrlClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var box = this.FindControl<TextBox>("RemoteUrlBox");
            var url = (box?.Text ?? string.Empty).Trim();
            _config.RemoteLibraryUrl = url;
            _remoteClient = null;
            SaveConfig();
            await BuildPlaylistAsync();
            PlayNext();
        }
        catch (Exception ex) { Console.WriteLine($"Apply remote URL failed: {ex.Message}"); }
    }

    private async void OnClearRemoteUrlClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var box = this.FindControl<TextBox>("RemoteUrlBox");
            if (box != null) box.Text = string.Empty;
            _config.RemoteLibraryUrl = string.Empty;
            _remoteClient = null;
            _remoteStatus = string.Empty;
            SaveConfig();
            await BuildPlaylistAsync();
            PlayNext();
        }
        catch (Exception ex) { Console.WriteLine($"Clear remote URL failed: {ex.Message}"); }
    }

    private async void OnSyncNowClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_isSyncing) return;
        if (string.IsNullOrWhiteSpace(_config.RemoteLibraryUrl))
        {
            await ShowMessageAsync("Set a Remote library URL first, then press Sync.");
            return;
        }
        _isSyncing = true;
        ShowSyncHud(true);
        try
        {
            EnsureRemoteClient();
            var progress = new Progress<SyncProgress>(p =>
            {
                _remoteStatus = p.Message ?? string.Empty;
                UpdateSyncHud(p);
                UpdateStatus();
            });
            var result = await _remoteClient!.SyncAsync(progress);
            await BuildPlaylistAsync();
            if (_mediaPlayer != null && !_mediaPlayer.IsPlaying)
                PlayNext();

            if (result.FilesSkipped > 0)
            {
                var shortBy = Math.Max(0, result.BytesNeeded - Math.Max(0, result.FreeBytes));
                await ShowMessageAsync(
                    $"Sync finished with skipped files.\n\n" +
                    $"Downloaded: {result.FilesDownloaded}\n" +
                    $"Skipped (insufficient disk space): {result.FilesSkipped}\n" +
                    $"Needed: {RemoteLibraryClient.FormatBytes(result.BytesNeeded)}\n" +
                    $"Free on target drive: {RemoteLibraryClient.FormatBytes(result.FreeBytes)}\n" +
                    $"Short by approximately: {RemoteLibraryClient.FormatBytes(shortBy)}");
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync($"Sync failed: {ex.Message}");
        }
        finally
        {
            _isSyncing = false;
            ShowSyncHud(false);
        }
    }

    private void ShowSyncHud(bool visible)
    {
        try
        {
            var hud = this.FindControl<Popup>("SyncHudPopup");
            if (hud != null) hud.IsOpen = visible;
        }
        catch { }
    }

    private void UpdateSyncHud(SyncProgress p)
    {
        try
        {
            var text = this.FindControl<TextBlock>("SyncHudText");
            var bar = this.FindControl<ProgressBar>("SyncHudBar");
            var detail = this.FindControl<TextBlock>("SyncHudDetail");
            if (text != null) text.Text = string.IsNullOrEmpty(p.Message) ? "Syncing…" : p.Message;
            if (bar != null)
            {
                if (p.BytesNeeded > 0)
                {
                    bar.IsIndeterminate = false;
                    bar.Value = Math.Clamp(100.0 * p.BytesDownloaded / p.BytesNeeded, 0, 100);
                }
                else
                {
                    bar.IsIndeterminate = !p.Done;
                }
            }
            if (detail != null)
            {
                if (p.FilesTotal > 0)
                {
                    var done = p.FilesDownloaded + p.FilesSkipped;
                    detail.Text = $"{done}/{p.FilesTotal} files   {RemoteLibraryClient.FormatBytes(p.BytesDownloaded)} / {RemoteLibraryClient.FormatBytes(p.BytesNeeded)}"
                                  + (p.FilesSkipped > 0 ? $"   ({p.FilesSkipped} skipped)" : string.Empty);
                }
                else
                {
                    detail.Text = string.Empty;
                }
            }
        }
        catch { }
    }

    // Reuse the SyncHudPopup to show progress during the initial library scan.
    // Indeterminate bar + a rolling file count -- the scan is recursive yield
    // so we never know the total upfront, but the count rising tells the user
    // the app is working, not hung.
    private void ShowScanHud(bool visible) => ShowSyncHud(visible);

    private void UpdateScanHud(string message, string folder, int count)
    {
        try
        {
            var text = this.FindControl<TextBlock>("SyncHudText");
            var bar = this.FindControl<ProgressBar>("SyncHudBar");
            var detail = this.FindControl<TextBlock>("SyncHudDetail");
            if (text != null) text.Text = message;
            if (bar != null) bar.IsIndeterminate = true;
            if (detail != null)
            {
                var folderShort = string.IsNullOrEmpty(folder) ? string.Empty : $"  ({folder})";
                detail.Text = $"{count:N0} files found{folderShort}";
            }
        }
        catch { }
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
                OpenExternalAndYieldFocus(path);
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
                    OpenExternalAndYieldFocus(folder);
                }
            }
        }
        catch { }
    }

    private bool _restoreFullScreenOnActivate;
    private void OpenExternalAndYieldFocus(string path)
    {
        // The Pinscreen window is fullscreen + topmost (and the clock is in a
        // topmost popup), so OS-launched apps like Notepad/Explorer open behind
        // it. Drop topmost, exit fullscreen, hide the overlay+clock popups, and
        // minimize so the new window can take focus. When the user comes back
        // to Pinscreen (taskbar click / alt-tab), the Activated handler below
        // puts it back into fullscreen.
        try { ToggleOverlay(false); } catch { }
        try { if (ClockPopup != null) ClockPopup.IsOpen = false; } catch { }
        try { this.Topmost = false; } catch { }
        try { if (WindowState == WindowState.FullScreen) WindowState = WindowState.Normal; } catch { }
        _restoreFullScreenOnActivate = true;
        try { WindowState = WindowState.Minimized; } catch { }
        OpenWithOS(path);
    }

    private void OnWindowActivatedRestoreFullScreen(object? sender, EventArgs e)
    {
        if (!_restoreFullScreenOnActivate) return;
        _restoreFullScreenOnActivate = false;
        Dispatcher.UIThread.Post(() =>
        {
            try { WindowState = WindowState.FullScreen; } catch { }
            try { this.Topmost = true; } catch { }
            try { if (ClockPopup != null) ClockPopup.IsOpen = true; } catch { }
            try { UpdateClock(); } catch { }
        }, DispatcherPriority.Background);
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
                _remoteClient = null; // sync target follows the media folder
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

    private async void OnCheckUpdatesClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Lightweight version check: ask GitHub for the latest release, compare
        // to the running version, and display the result. No download, no
        // self-update -- the user installs the new version themselves from the
        // releases page (matches the jjp-asset-decryptor pattern).
        string current = FormatVersion(GetLocalVersion());
        string releasesUrl = $"https://github.com/{GitHubUpdateRepo}/releases";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Pinscreen2-UpdateCheck");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await http.GetAsync($"https://api.github.com/repos/{GitHubUpdateRepo}/releases/latest");
            if (!resp.IsSuccessStatusCode)
            {
                await ShowMessageAsync(
                    $"Update check failed: {(int)resp.StatusCode} {resp.ReasonPhrase}\n\n" +
                    $"Current version: {current}\n" +
                    $"Releases: {releasesUrl}");
                return;
            }
            var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync()).RootElement;
            var tag = doc.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            var name = doc.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
            var htmlUrl = doc.TryGetProperty("html_url", out var u) ? (u.GetString() ?? releasesUrl) : releasesUrl;
            var publishedAt = doc.TryGetProperty("published_at", out var p) ? (p.GetString() ?? "") : "";

            var latest = ParseVersion(tag);
            var local = GetLocalVersion();
            string latestDisplay = latest != null ? FormatVersion(latest) : (string.IsNullOrWhiteSpace(tag) ? "(unknown)" : tag);
            string verdict;
            if (latest != null && local != null && latest > local)
                verdict = $"Update available: {latestDisplay}";
            else if (latest != null && local != null && latest <= local)
                verdict = "You're up to date.";
            else
                verdict = $"Latest release: {latestDisplay}";

            var msg = verdict + "\n\n" +
                      $"Current version: {current}\n" +
                      $"Latest version:  {latestDisplay}" +
                      (string.IsNullOrWhiteSpace(name) || name == tag ? "" : $"  ({name})") + "\n" +
                      (string.IsNullOrWhiteSpace(publishedAt) ? "" : $"Published:       {publishedAt}\n");
            await ShowMessageAsync(msg, htmlUrl);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(
                $"Update check failed: {ex.Message}\n\n" +
                $"Current version: {current}",
                releasesUrl);
        }
    }

    private async void OnPreviewQueueClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        const int Cap = 25;
        try
        {
            var snapshot = _playlist.Take(Cap).ToList();
            string nowName = string.IsNullOrWhiteSpace(_currentItem) ? "(none)" : DisplayNameForItem(_currentItem);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Now playing: {nowName}");
            sb.AppendLine();
            if (snapshot.Count == 0)
            {
                sb.AppendLine("Queue is empty.");
            }
            else
            {
                sb.AppendLine($"Up next ({snapshot.Count}{(_playlist.Count > Cap ? $" of {_playlist.Count}" : "")}):");
                for (int i = 0; i < snapshot.Count; i++)
                    sb.AppendLine($"  {i + 1,2}. {DisplayNameForItem(snapshot[i])}");
                if (_playlist.Count > Cap)
                    sb.AppendLine($"  … +{_playlist.Count - Cap} more");
            }
            await ShowMessageAsync(sb.ToString());
        }
        catch (Exception ex)
        {
            await ShowMessageAsync($"Could not build queue preview: {ex.Message}");
        }
    }

    private static string DisplayNameForItem(string item)
    {
        if (string.IsNullOrWhiteSpace(item)) return "(none)";
        try
        {
            var file = Path.GetFileName(item);
            var game = new DirectoryInfo(Path.GetDirectoryName(item) ?? string.Empty).Name;
            return string.IsNullOrEmpty(game) ? file : $"{game} / {file}";
        }
        catch { return item; }
    }

    private static Version? GetLocalVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var parsed = ParseVersion(info);
            if (parsed != null && parsed != new Version(1, 0, 0, 0)) return parsed;
            var asmVer = asm.GetName().Version;
            if (asmVer != null && asmVer != new Version(1, 0, 0, 0)) return asmVer;

            // Fall back to the EXE's FileVersionInfo (stamped by the installer
            // and by Version.props if available). This catches builds where the
            // managed assembly was not stamped via -p:Version.
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                {
                    var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
                    var fileVer = ParseVersion(fvi.ProductVersion) ?? ParseVersion(fvi.FileVersion);
                    if (fileVer != null && fileVer != new Version(1, 0, 0, 0)) return fileVer;
                }
            }
            catch { }
            return parsed ?? asmVer;
        }
        catch { return null; }
    }

    private static Version? ParseVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var s = v.Trim().TrimStart('v', 'V');
        // Strip semver pre-release / build metadata (e.g. "1.7.2+abc" or "1.7.2-rc1")
        // so System.Version.TryParse accepts them.
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s.Substring(0, plus);
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s.Substring(0, dash);
        Version ver;
        return Version.TryParse(s, out ver) ? ver : null;
    }

    // Render a Version as "v<major>.<minor>.<build>", trimming any trailing .0
    // segments so "1.7.1.0" displays as "v1.7.1" -- matches the GitHub tag style.
    private static string FormatVersion(Version? v)
    {
        if (v == null) return "unknown";
        int major = Math.Max(0, v.Major);
        int minor = Math.Max(0, v.Minor);
        int build = Math.Max(0, v.Build);
        int rev   = Math.Max(0, v.Revision);
        if (rev > 0) return $"v{major}.{minor}.{build}.{rev}";
        return $"v{major}.{minor}.{build}";
    }

    private Task ShowMessageAsync(string text) => ShowMessageAsync(text, null);

    private async Task ShowMessageAsync(string text, string? linkUrl)
    {
        try
        {
            // Suppress overlay auto-open while dialog is visible
            var prevSuppress = _suppressOverlayOpen;
            _suppressOverlayOpen = true;
            try { if (OverlayPopup != null) OverlayPopup.IsOpen = false; } catch { }

            var body = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            };
            var buttonBar = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 12, 0, 0),
            };
            if (!string.IsNullOrWhiteSpace(linkUrl))
            {
                var openBtn = new Button
                {
                    Content = "Open in browser",
                    Foreground = Brushes.White,
                };
                openBtn.Click += (_, __) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = linkUrl!,
                            UseShellExecute = true,
                        });
                    }
                    catch { }
                };
                buttonBar.Children.Add(openBtn);
            }
            var okBtn = new Button
            {
                Content = "OK",
                Foreground = Brushes.White,
            };
            buttonBar.Children.Add(okBtn);
            var dlg = new Window
            {
                Width = 640,
                Title = "Pinscreen 2",
                CanResize = true,
                Topmost = true,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.Height,
                MinHeight = 160,
                MaxHeight = 600,
                Background = Brushes.Black,
                Content = new DockPanel
                {
                    Margin = new Thickness(16),
                    Children =
                    {
                        new DockPanel
                        {
                            [DockPanel.DockProperty] = Dock.Bottom,
                            Children = { buttonBar },
                        },
                        new ScrollViewer
                        {
                            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                            Content = body,
                        },
                    },
                },
            };
            okBtn.Click += (_, __) => dlg.Close();
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

    private bool _updatingClock;
    private void UpdateClock()
    {
        if (_updatingClock) return;
        _updatingClock = true;
        try { UpdateClockCore(); }
        finally { _updatingClock = false; }
    }

    private void UpdateClockCore()
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
                // Position clock based on percentage within the clock's actual container
                try
                {
                    var container = ClockText.Parent as Control;
                    var root = this.FindControl<Grid>("RootGrid");
                    double cw = container?.Bounds.Width ?? 0;
                    double ch = container?.Bounds.Height ?? 0;
                    // Fall back to the window/root if the container hasn't laid out yet
                    if ((cw <= 1 || ch <= 1) && root != null)
                    {
                        cw = root.Bounds.Width;
                        ch = root.Bounds.Height;
                    }
                    if (cw > 1 && ch > 1)
                    {
                        ClockText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        var textSize = ClockText.DesiredSize;
                        var width = Math.Max(0, cw - (ClockEdgePadding * 2));
                        var height = Math.Max(0, ch - (ClockEdgePadding * 2));
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

    private async Task BuildPlaylistAsync() => await BuildPlaylistAsync(useCache: false);

    private async Task BuildPlaylistAsync(bool useCache)
    {
        // (path, size). Size is captured during scan or loaded from cache so
        // the bucket-by-size grouping below doesn't need to stat every file.
        var collected = new List<(string Path, long Size)>();
        int totalFound = 0;

        // Single source of truth: the configured media folders. When a remote
        // library URL is set, Sync downloads INTO the first media folder, and
        // a regular scan picks the files up just like local content.
        var folders = _config.MediaFolders.ToList();
        var resolvedFolders = folders
            .Select(ResolveFolderPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();

        bool builtFromCache = false;
        if (useCache)
        {
            var cached = TryLoadPlaylistCache(resolvedFolders);
            if (cached != null && cached.Count > 0)
            {
                collected.AddRange(cached);
                totalFound = cached.Count;
                builtFromCache = true;
                Console.WriteLine($"Playlist cache hit: {cached.Count} files (folders={string.Join(",", resolvedFolders)})");
                // Refresh the cache in the background so next launch has fresh
                // data. Don't rebuild the running playlist -- predictable
                // behavior is to only change the queue on explicit Rebuild.
                _ = Task.Run(() => RefreshPlaylistCache(resolvedFolders));
            }
        }

        if (!builtFromCache)
        {
            // Show a progress HUD during the scan -- on first launch with a
            // big library this is the only thing that takes any noticeable
            // time and we don't want it to look like the app is hung.
            string currentFolder = string.Empty;
            int scannedCount = 0;
            ShowScanHud(true);
            UpdateScanHud("Scanning library…", currentFolder, scannedCount);
            var hudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            hudTimer.Tick += (_, __) =>
                UpdateScanHud("Scanning library…", currentFolder, Volatile.Read(ref scannedCount));
            hudTimer.Start();

            try
            {
                await Task.Run(() =>
                {
                    foreach (var resolved in resolvedFolders)
                    {
                        if (!Directory.Exists(resolved))
                        {
                            Console.WriteLine($"Scan skip: folder not found -> '{resolved}'");
                            continue;
                        }
                        Console.WriteLine($"Scanning: {resolved}");
                        currentFolder = resolved;

                        foreach (var file in EnumerateVideoFilesSafe(resolved))
                        {
                            long size = 0;
                            try { size = new FileInfo(file).Length; } catch { }
                            collected.Add((file, size));
                            totalFound++;
                            Interlocked.Increment(ref scannedCount);
                        }
                    }
                });
                try { SavePlaylistCache(resolvedFolders, collected); } catch { }
            }
            finally
            {
                hudTimer.Stop();
                ShowScanHud(false);
            }
        }

        // Build the final order. Replaces the previous deterministic
        // round-robin (which produced a noticeable "always the same pattern"
        // even when individual items shuffled) with a constrained random
        // shuffle: pick the next item uniformly at random from items whose
        // group key isn't among the last RecentWindow picks. Falls back to
        // any random item when constraints can't be satisfied. Result is
        // genuinely different every run while still spreading same-game and
        // same-format items out.
        var rng = Random.Shared;
        IEnumerable<string> finalOrder;
        if (_config.BalanceQueueByGame)
        {
            const int RecentWindow = 5;
            var pool = collected
                .Select(it => (it.Path, Key: GetGroupKey(it.Path, it.Size)))
                .ToList();
            // Fisher-Yates shuffle so initial scan order doesn't bias selection.
            for (int i = pool.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }

            var output = new List<string>(pool.Count);
            var recent = new Queue<string>();
            while (pool.Count > 0)
            {
                int pickIdx = -1;
                // First pass: random search for a non-conflicting item.
                int attempts = Math.Min(pool.Count, 32);
                for (int t = 0; t < attempts; t++)
                {
                    int candidate = rng.Next(pool.Count);
                    if (!recent.Contains(pool[candidate].Key))
                    {
                        pickIdx = candidate;
                        break;
                    }
                }
                // Fallback: linear scan for any non-conflicting item.
                if (pickIdx < 0)
                {
                    for (int i = 0; i < pool.Count; i++)
                    {
                        if (!recent.Contains(pool[i].Key)) { pickIdx = i; break; }
                    }
                }
                // Last resort: pool entirely from recent groups; just pick one.
                if (pickIdx < 0) pickIdx = rng.Next(pool.Count);

                var picked = pool[pickIdx];
                // Swap-remove for O(1).
                pool[pickIdx] = pool[pool.Count - 1];
                pool.RemoveAt(pool.Count - 1);

                output.Add(picked.Path);
                recent.Enqueue(picked.Key);
                if (recent.Count > RecentWindow) recent.Dequeue();
            }
            finalOrder = output;
        }
        else
        {
            // Plain Fisher-Yates uniform shuffle.
            var arr = collected.Select(it => it.Path).ToArray();
            for (int i = arr.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (arr[i], arr[j]) = (arr[j], arr[i]);
            }
            finalOrder = arr;
        }

        _playlist.Clear();
        foreach (var f in finalOrder)
            _playlist.Enqueue(f);

        Console.WriteLine($"Scan complete: {_playlist.Count} files queued (found {totalFound})");
        UpdateStatus();
        // If the VideoView already attached and was waiting for a playlist,
        // start playback now. Idempotent.
        try { Dispatcher.UIThread.Post(TryStartPlayback); } catch { }
    }

    // ---- Playlist cache --------------------------------------------------
    // Persisted at %LOCALAPPDATA%\Pinscreen2\playlist.cache.json. On startup
    // we use the cache to build the playlist instantly, then refresh it in
    // the background. Saves several seconds on launches with large libraries.

    private class PlaylistCacheItem
    {
        public string P { get; set; } = string.Empty; // path
        public long S { get; set; }                    // size bytes
    }

    private class PlaylistCacheDto
    {
        public List<string> Folders { get; set; } = new();
        // Legacy schema (v1.7.5-1.7.8): list of paths with no sizes.
        public List<string>? Files { get; set; }
        // New schema: list of {P=path, S=size}.
        public List<PlaylistCacheItem>? Items { get; set; }
        public string SavedAt { get; set; } = string.Empty;
    }

    private static string PlaylistCachePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pinscreen2");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "playlist.cache.json");
    }

    // True when a usable playlist cache exists for the given folder set --
    // used at startup to decide whether to load instantly or prompt the user
    // to kick off the first scan.
    private static bool HasPlaylistCache(List<string> currentFolders)
    {
        try
        {
            var loaded = TryLoadPlaylistCache(currentFolders);
            return loaded != null && loaded.Count > 0;
        }
        catch { return false; }
    }

    // Loaded cache is returned as (path, size) pairs so the playlist build does
    // NOT need to stat each file again to bucket by size. This is the main
    // startup cost on libraries with tens of thousands of files.
    private static List<(string Path, long Size)>? TryLoadPlaylistCache(List<string> currentFolders)
    {
        try
        {
            var path = PlaylistCachePath();
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<PlaylistCacheDto>(json);
            if (dto == null) return null;
            // Cache is only valid if the folder set matches exactly.
            var a = dto.Folders.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            var b = currentFolders.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            if (a.Count != b.Count) return null;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return null;

            // Don't File.Exists every entry on load -- 30k stats negates the
            // point of the cache. Trust it; if a few files are gone, VLC will
            // skip them and the background refresh will rebuild without them.
            if (dto.Items != null && dto.Items.Count > 0)
                return dto.Items.Select(it => (it.P, it.S)).ToList();
            if (dto.Files != null && dto.Files.Count > 0)
                return dto.Files.Select(p => (p, 0L)).ToList(); // legacy: size unknown
            return null;
        }
        catch { return null; }
    }

    private static void SavePlaylistCache(List<string> folders, List<(string Path, long Size)> items)
    {
        try
        {
            var dto = new PlaylistCacheDto
            {
                Folders = folders,
                Items = items.Select(it => new PlaylistCacheItem { P = it.Path, S = it.Size }).ToList(),
                SavedAt = DateTime.UtcNow.ToString("O"),
            };
            File.WriteAllText(PlaylistCachePath(),
                JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch { }
    }

    // Walks the folders, captures each file's size, writes the cache. Stat
    // cost is paid here (background) instead of on the next startup.
    private static void RefreshPlaylistCache(List<string> folders)
    {
        try
        {
            var fresh = new List<(string Path, long Size)>();
            foreach (var f in folders)
            {
                if (!Directory.Exists(f)) continue;
                foreach (var path in EnumerateVideoFilesSafe(f))
                {
                    long size = 0;
                    try { size = new FileInfo(path).Length; } catch { }
                    fresh.Add((path, size));
                }
            }
            SavePlaylistCache(folders, fresh);
            Console.WriteLine($"Playlist cache refreshed: {fresh.Count} files");
        }
        catch { }
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

    private static string GetGroupKey(string item, long size)
    {
        // Group by (game, coarse-size-bucket) so multi-format games (e.g. tiny
        // amber-DMD strips alongside full-resolution LCD clips) interleave by
        // both game AND visual format. File size is a cheap proxy for format.
        // Size is supplied by the caller (from the cache or scan) so this is
        // O(1) -- no per-file stat.
        var game = new DirectoryInfo(Path.GetDirectoryName(item) ?? string.Empty).Name;
        // log2 bucket grouped by 2 powers: <512KB, <2MB, <8MB, <32MB, <128MB, ≥128MB
        int bucket = size <= 0 ? 0 : (int)Math.Floor(Math.Log2(size) / 2.0);
        return game + "|" + bucket;
    }

    private void EnsureRemoteClient()
    {
        if (_remoteClient != null) return;
        // Sync writes directly into the (first) media folder; there is no
        // separate sync cache. The folder is the single source of truth.
        var first = _config.MediaFolders.FirstOrDefault();
        var resolved = string.IsNullOrWhiteSpace(first)
            ? RemoteLibraryClient.DefaultCacheDir()
            : ResolveFolderPath(first!);
        if (string.IsNullOrWhiteSpace(resolved))
            resolved = RemoteLibraryClient.DefaultCacheDir();
        _remoteClient = new RemoteLibraryClient(_config.RemoteLibraryUrl, resolved);
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
        var item = _currentItem;
        var delay = TimeSpan.FromSeconds(_delaySeconds);

        Dispatcher.UIThread.Post(async () =>
        {
            try { _mediaPlayer?.Stop(); } catch { }
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay); } catch { }
            }
            if (_mediaPlayer == null || _libVlc == null) return;
            if (item != _currentItem) return; // user advanced past us
            var media = new Media(_libVlc, item, FromType.FromPath);
            _mediaPlayer.Play(media);
        }, DispatcherPriority.Background);

        UpdateStatus();
    }

    // No VLC marquee positioning; Avalonia clock overlay only

    private void UpdateStatus()
    {
        try
        {
            var status = this.FindControl<TextBlock>("StatusText");
            if (status == null) return;
            var source = string.IsNullOrWhiteSpace(_config.RemoteLibraryUrl)
                ? (_config.MediaFolders.FirstOrDefault() ?? "(none)")
                : $"remote:{_config.RemoteLibraryUrl}";
            var vlcStatus = _libVlcInitFailed ? "VLC: missing" : (_libVlc == null ? "VLC: not ready" : "VLC: ok");
            var cfgSrc = (!string.IsNullOrWhiteSpace(_effectiveConfigPath) && _effectiveConfigPath.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)) ? "cfg:app" : "cfg:user";
            var nowName = Path.GetFileName(_currentItem);
            var remote = string.IsNullOrEmpty(_remoteStatus) ? string.Empty : $"   {_remoteStatus}";
            status.Text = $"Source: {source}   Queue: {_playlist.Count}   Now: {nowName}   {vlcStatus}   {cfgSrc}{remote}";
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
    public string RemoteLibraryUrl { get; set; } = string.Empty;
    // LibVLC video output module override. Empty = platform default
    // ("direct3d9" on Windows; let VLC pick elsewhere). Set to e.g.
    // "direct3d11", "wingdi", or "gl" to work around driver-specific crashes.
    public string VlcVideoOutput { get; set; } = string.Empty;
}