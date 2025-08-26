using Avalonia.Controls;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

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
        Core.Initialize();
        LoadConfig();
        SetupClock();
        await BuildPlaylistAsync();

        _libVlc = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVlc);
        VideoView.MediaPlayer = _mediaPlayer;
        _mediaPlayer.EndReached += (_, __) => Dispatcher.UIThread.Post(PlayNext);

        PlayNext();
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
}