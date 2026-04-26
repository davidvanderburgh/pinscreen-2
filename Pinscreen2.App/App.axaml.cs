using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Pinscreen2.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Catch otherwise-fatal exceptions and write them to a crash log so we
        // can diagnose intermittent failures (e.g. on monitor wake) instead of
        // the process just disappearing.
        WireCrashLogging();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string CrashLogPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pinscreen2");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "crash.log");
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}{Environment.NewLine}";
            File.AppendAllText(CrashLogPath(), line);
            Console.Error.WriteLine(line);
        }
        catch { }
    }

    private static void WireCrashLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
        try
        {
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                LogCrash("Dispatcher.UIThread.UnhandledException", e.Exception);
                e.Handled = true;
            };
        }
        catch { }
    }
}