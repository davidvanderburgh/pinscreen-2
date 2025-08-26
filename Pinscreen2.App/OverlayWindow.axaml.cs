using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Pinscreen2.App;

public partial class OverlayWindow : Window
{
    private readonly DispatcherTimer _clockTimer = new DispatcherTimer();
    private readonly MainWindow _host;

    public OverlayWindow(MainWindow host)
    {
        InitializeComponent();
        _host = host;

        _clockTimer.Interval = System.TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, __) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();
    }

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        Topmost = true;
        SyncToHost();
    }

    private void SyncToHost()
    {
        try
        {
            Position = _host.Position;
            Width = _host.Bounds.Width;
            Height = _host.Bounds.Height;
        }
        catch { }
    }

    private void UpdateClock()
    {
        ClockText.Text = System.DateTime.Now.ToString(_host.Config.ClockFormat);
        SyncToHost();
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ToggleOverlay();
    }

    private void OnOverlayBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        ToggleOverlay(force: false);
        e.Handled = true;
    }

    private void OnOverlayPanelPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private void ToggleOverlay(bool? force = null)
    {
        var newState = force ?? !OverlayBackdrop.IsVisible;
        OverlayBackdrop.IsVisible = newState;
        Topmost = true;
    }

    private void OnPlayPause(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _host.PlayPauseCommand();
    private void OnNext(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _host.NextCommand();
    private void OnRebuildQueue(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _host.RebuildQueueCommand();
    private void OnOpenConfig(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _host.OpenConfigCommand();
    private void OnOpenCurrentFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _host.OpenCurrentFolderCommand();
    private void OnSetMediaFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _host.SetMediaFolderCommand();
    private void OnQuit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _host.QuitCommand();
}

