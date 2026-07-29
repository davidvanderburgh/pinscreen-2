using System;
using System.IO;
using System.Text;

namespace Pinscreen2.App;

/// <summary>
/// Tees Console output to a rolling log file.
///
/// The app is a WinExe with no console, so every Console.WriteLine in it went
/// nowhere. Two hangs on a wall-mounted screen had to be diagnosed by inference
/// from a photograph, which is not a debugging strategy. Everything now lands in
/// %LOCALAPPDATA%\Pinscreen2\app.log, one prior generation kept.
/// </summary>
public sealed class AppLog : TextWriter
{
    private const long MaxBytes = 4L * 1024 * 1024;

    private readonly TextWriter? _console;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly StringBuilder _pending = new();
    private StreamWriter? _file;
    private long _written;

    public override Encoding Encoding => Encoding.UTF8;

    private AppLog(TextWriter? console, string path)
    {
        _console = console;
        _path = path;
        Open(truncate: false);
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pinscreen2", "app.log");

    public static void Install()
    {
        try
        {
            var path = DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Roll(path);
            var log = new AppLog(Console.Out, path);
            Console.SetOut(log);
            Console.SetError(log);
            Console.WriteLine($"=== Pinscreen 2 starting (pid {Environment.ProcessId}) ===");
        }
        catch { /* logging must never prevent the app from running */ }
    }

    private static void Roll(string path)
    {
        try { if (File.Exists(path)) File.Move(path, path + ".prev", overwrite: true); }
        catch { }
    }

    private void Open(bool truncate)
    {
        try
        {
            _file = new StreamWriter(new FileStream(_path,
                truncate ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            { AutoFlush = true };
            _written = truncate ? 0 : new FileInfo(_path).Length;
        }
        catch { _file = null; }
    }

    public override void Write(char value) => Append(value.ToString());
    public override void Write(string? value) => Append(value);
    public override void WriteLine(string? value) => Append((value ?? string.Empty) + "\n");

    private void Append(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            try { _console?.Write(text); } catch { }
            _pending.Append(text);
            int nl;
            while ((nl = IndexOfNewline(_pending)) >= 0)
            {
                var line = _pending.ToString(0, nl).TrimEnd('\r');
                _pending.Remove(0, nl + 1);
                Emit(line);
            }
        }
    }

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++) if (sb[i] == '\n') return i;
        return -1;
    }

    private void Emit(string value)
    {
        if (_file == null) return;
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {value}";
            _file.WriteLine(line);
            _written += line.Length + 2;
            if (_written > MaxBytes)
            {
                _file.Dispose();
                _file = null;
                Roll(_path);
                Open(truncate: true);
            }
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _file?.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
