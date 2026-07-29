using System.Text.Json;

namespace Pinscreen2.Server;

public record ManifestItem(string Path, long Size);
public record Manifest(List<ManifestItem> Files);

public record GameSummary(string Name, int Files, long Bytes);

/// <summary>
/// Owns the video library view: the cached manifest bytes served to pinscreens
/// and the grouped-by-game summary the dashboard renders.
///
/// Walking 36k+ files takes seconds and pegs the CPU, so it is done once on a
/// timer and served from memory. <see cref="Refresh"/> forces a rebuild.
/// </summary>
public class LibraryService
{
    private static readonly string[] VideoExts = { ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".webm" };

    private readonly string _root;
    private readonly object _lock = new();

    private byte[]? _manifestBytes;
    private List<ManifestItem> _items = new();
    private DateTimeOffset _builtAt = DateTimeOffset.MinValue;
    private long _totalBytes;

    public LibraryService(string root) => _root = root;

    public string Root => _root;
    public DateTimeOffset BuiltAt { get { lock (_lock) return _builtAt; } }
    public int FileCount { get { lock (_lock) return _items.Count; } }
    public long TotalBytes { get { lock (_lock) return _totalBytes; } }

    public byte[]? ManifestBytes { get { lock (_lock) return _manifestBytes; } }

    /// <summary>False until the first scan completes.</summary>
    public bool IsReady { get { lock (_lock) return _manifestBytes != null; } }

    private List<ManifestItem> Scan()
    {
        var items = new List<ManifestItem>();
        var stack = new Stack<string>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var sd in Directory.EnumerateDirectories(dir)) stack.Push(sd);
            }
            catch { }
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (Array.IndexOf(VideoExts, ext) < 0) continue;
                    var rel = Path.GetRelativePath(_root, f).Replace('\\', '/');
                    long size = 0;
                    try { size = new FileInfo(f).Length; } catch { }
                    items.Add(new ManifestItem(rel, size));
                }
            }
            catch { }
        }
        return items;
    }

    /// <summary>Rebuilds the manifest. Returns true if the content changed.</summary>
    public bool Refresh()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var items = Scan();
            // Default (PascalCase) naming is the established wire format for
            // manifest.json -- deployed clients match case-insensitively, but
            // don't churn it needlessly.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Manifest(items));
            sw.Stop();

            bool changed;
            lock (_lock)
            {
                changed = _manifestBytes == null || !_manifestBytes.AsSpan().SequenceEqual(bytes);
                _manifestBytes = bytes;
                _items = items;
                _totalBytes = items.Sum(i => i.Size);
                _builtAt = DateTimeOffset.UtcNow;
            }
            Console.WriteLine($"Manifest refreshed: {bytes.Length} bytes in {sw.ElapsedMilliseconds} ms" +
                              (changed ? " (changed)" : ""));
            return changed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Manifest refresh failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Files grouped by their top-level folder, which is the game name.</summary>
    public List<GameSummary> Games()
    {
        List<ManifestItem> snapshot;
        lock (_lock) snapshot = _items;

        return snapshot
            .GroupBy(i =>
            {
                var idx = i.Path.IndexOf('/');
                return idx < 0 ? "(loose files)" : i.Path[..idx];
            }, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GameSummary(g.Key, g.Count(), g.Sum(i => i.Size)))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Files belonging to one game folder.</summary>
    public List<ManifestItem> GameFiles(string game)
    {
        List<ManifestItem> snapshot;
        lock (_lock) snapshot = _items;

        var prefix = game + "/";
        return snapshot
            .Where(i => i.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Resolves a manifest-relative path to a real file, refusing escapes.</summary>
    public string? ResolveFile(string relPath)
    {
        var decoded = relPath.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_root, decoded));
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(full) ? full : null;
    }
}
