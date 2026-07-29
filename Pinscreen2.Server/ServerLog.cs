using System.Text;

namespace Pinscreen2.Server;

/// <summary>
/// Tees Console output to a rolling log file so the server can be launched
/// directly by Task Scheduler -- no cmd wrapper doing shell redirection, which
/// means the scheduled task stays "running" for as long as the process lives
/// and the scheduler can restart it when it dies.
/// </summary>
public sealed class ServerLog : TextWriter
{
    // The 5-minute refresh line alone is ~20 MB/year; roll well before that.
    private const long MaxBytes = 5L * 1024 * 1024;

    private readonly TextWriter _console;
    private readonly string _path;
    private readonly object _gate = new();
    private StreamWriter? _file;
    private long _written;

    public override Encoding Encoding => Encoding.UTF8;

    private ServerLog(TextWriter console, string path)
    {
        _console = console;
        _path = path;
        Open(truncate: false);
    }

    /// <summary>Redirects Console.Out/Error to a tee over <paramref name="path"/>.</summary>
    public static void Install(string path)
    {
        try
        {
            // Start each run against the previous generation, matching the old
            // launcher script's behavior.
            Roll(path);
            var log = new ServerLog(Console.Out, path);
            Console.SetOut(log);
            Console.SetError(log);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Log file init failed ({ex.Message}); continuing with console only.");
        }
    }

    private static void Roll(string path)
    {
        try
        {
            if (File.Exists(path)) File.Move(path, path + ".prev", overwrite: true);
        }
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

    // ASP.NET's console logger writes a character at a time, so buffer until a
    // newline instead of timestamping every char as its own line.
    private readonly StringBuilder _pending = new();

    public override void Write(char value) => Append(value.ToString());
    public override void Write(string? value) => Append(value);
    public override void WriteLine(string? value) => Append((value ?? string.Empty) + "\n");

    private void Append(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            try { _console.Write(text); } catch { }

            _pending.Append(text);
            int nl;
            while ((nl = IndexOfNewline(_pending)) >= 0)
            {
                var line = _pending.ToString(0, nl).TrimEnd('\r');
                _pending.Remove(0, nl + 1);
                EmitLine(line);
            }
        }
    }

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++) if (sb[i] == '\n') return i;
        return -1;
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void EmitLine(string value)
    {
        if (_file == null) return;
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {value}";
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
