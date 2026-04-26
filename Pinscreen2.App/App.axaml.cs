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
    private static StreamWriter? _stdLog;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Diagnostics: pipe stdout/stderr (including LibVLC native output) to a
        // log file, write a startup line, and wire managed unhandled-exception
        // handlers. Native crashes (LibVLC, OpenGL drivers) bypass the .NET
        // handlers, so the stdout/stderr log is often the only forensic trail.
        WireDiagnostics();
        AppDomain.CurrentDomain.ProcessExit += (_, __) => Log("ProcessExit (clean shutdown)");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.Exit += (_, __) => Log("Desktop.Exit");
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string LogDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pinscreen2");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string CrashLogPath() => Path.Combine(LogDir(), "crash.log");
    private static string AppLogPath()   => Path.Combine(LogDir(), "app.log");

    private static void Log(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            File.AppendAllText(AppLogPath(), line);
        }
        catch { }
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}{Environment.NewLine}";
            File.AppendAllText(CrashLogPath(), line);
            File.AppendAllText(AppLogPath(), line);
        }
        catch { }
    }

    private static void WireDiagnostics()
    {
        // Tee stdout + stderr to app.log. LibVLC writes diagnostics to stderr
        // before it dies, which is usually the only clue a native crash gives.
        try
        {
            var fs = new FileStream(AppLogPath(), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _stdLog = new StreamWriter(fs) { AutoFlush = true };
            Console.SetOut(new TeeWriter(Console.Out, _stdLog));
            Console.SetError(new TeeWriter(Console.Error, _stdLog));
        }
        catch { }

        Log($"=== App start === pid={Environment.ProcessId} version={typeof(App).Assembly.GetName().Version}");

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

    // Writes to two TextWriters at once (original console + log file).
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _a, _b;
        public TeeWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
        public override System.Text.Encoding Encoding => _a.Encoding;
        public override void Write(char value) { try { _a.Write(value); } catch { } try { _b.Write(value); } catch { } }
        public override void Write(string? value) { try { _a.Write(value); } catch { } try { _b.Write(value); } catch { } }
        public override void WriteLine(string? value) { try { _a.WriteLine(value); } catch { } try { _b.WriteLine(value); } catch { } }
        public override void Flush() { try { _a.Flush(); } catch { } try { _b.Flush(); } catch { } }
    }
}