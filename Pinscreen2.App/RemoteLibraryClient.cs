using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Pinscreen2.App;

public record RemoteFile(string Path, long Size);

/// <summary>What a single game still owes, for the dashboard's incoming list.</summary>
public record PendingGame(string Name, int Files, long Bytes);

public class SyncProgress
{
    public int FilesTotal { get; set; }
    public int FilesDownloaded { get; set; }
    public int FilesSkipped { get; set; }
    /// <summary>Files that exhausted their retries. The sync continues past them.</summary>
    public int FilesFailed { get; set; }
    public string? LastError { get; set; }
    public long BytesNeeded { get; set; }
    public long BytesDownloaded { get; set; }
    public long FreeBytes { get; set; }
    /// <summary>Manifest-relative path, so the game is its first segment.</summary>
    public string CurrentFile { get; set; } = string.Empty;
    public string? Message { get; set; }
    public bool Done { get; set; }

    /// <summary>
    /// Everything this sync intends to fetch, grouped by game and computed once
    /// up front, so the dashboard can show what is coming rather than only what
    /// is happening right now.
    /// </summary>
    public List<PendingGame> Pending { get; set; } = new();

    /// <summary>Game folder of <see cref="CurrentFile"/>, or empty for a loose file.</summary>
    public string CurrentGame => GameOf(CurrentFile);

    public static string GameOf(string relPath)
    {
        if (string.IsNullOrEmpty(relPath)) return string.Empty;
        var i = relPath.IndexOf('/');
        return i < 0 ? string.Empty : relPath[..i];
    }
}

public class RemoteLibraryClient
{
    // Keep some breathing room on the destination drive after syncing.
    private const long DiskHeadroomBytes = 1024L * 1024 * 1024; // 1 GB

    // Per-file retries before giving up on it and moving to the next.
    private const int MaxAttemptsPerFile = 3;
    private const int RetryDelayMs = 1500;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _cacheDir;
    private readonly Dictionary<string, Task<string>> _inFlight = new();
    private readonly object _gate = new();

    public string CacheDir => _cacheDir;

    public RemoteLibraryClient(string baseUrl, string cacheDir)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    public static string DefaultCacheDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pinscreen2", "cache");

    public async Task<List<RemoteFile>> FetchManifestAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"{_baseUrl}/manifest.json", ct);
        var manifest = JsonSerializer.Deserialize<ManifestDto>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return manifest?.Files?.Select(f => new RemoteFile(f.Path, f.Size)).ToList() ?? new List<RemoteFile>();
    }

    public string GetCachePath(string relPath)
    {
        var safe = relPath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(_cacheDir, safe);
    }

    public bool IsCached(string relPath, long expectedSize)
    {
        var p = GetCachePath(relPath);
        if (!File.Exists(p)) return false;
        if (expectedSize > 0)
        {
            try { return new FileInfo(p).Length == expectedSize; } catch { return false; }
        }
        return true;
    }

    /// <summary>What this screen is missing relative to the server, downloading nothing.</summary>
    public record PendingSummary(int Files, long Bytes, List<PendingGame> Games);

    /// <summary>
    /// Diffs the server manifest against the local cache without fetching
    /// anything, so the dashboard can answer "does this screen need a sync?"
    /// before anyone presses the button. Comparing total counts alone can't:
    /// two libraries of equal size are not necessarily the same library.
    ///
    /// Costs one manifest fetch (cached server-side) plus a stat per entry, so
    /// callers should run it on a background thread and not on a fast timer.
    /// </summary>
    public async Task<PendingSummary> ComputePendingAsync(CancellationToken ct = default)
    {
        var manifest = await FetchManifestAsync(ct);
        var missing = new List<RemoteFile>();
        foreach (var f in manifest)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsCached(f.Path, f.Size)) missing.Add(f);
        }

        var games = missing
            .GroupBy(f => SyncProgress.GameOf(f.Path) is { Length: > 0 } g ? g : "(loose files)",
                     StringComparer.OrdinalIgnoreCase)
            .Select(g => new PendingGame(g.Key, g.Count(), g.Sum(f => f.Size)))
            .OrderByDescending(g => g.Files)
            .ToList();

        return new PendingSummary(missing.Count, missing.Sum(f => f.Size), games);
    }

    public Task<string> EnsureCachedAsync(RemoteFile file, CancellationToken ct = default)
    {
        var local = GetCachePath(file.Path);
        if (IsCached(file.Path, file.Size)) return Task.FromResult(local);

        lock (_gate)
        {
            if (_inFlight.TryGetValue(file.Path, out var existing)) return existing;
            var t = DownloadAsync(file, local, ct);
            _inFlight[file.Path] = t;
            _ = t.ContinueWith(_ =>
            {
                lock (_gate) { _inFlight.Remove(file.Path); }
            }, TaskScheduler.Default);
            return t;
        }
    }

    /// <summary>
    /// Drops a completed-or-faulted download from the in-flight map. The
    /// ContinueWith that normally does this may not have run yet when the await
    /// throws, so a retry could otherwise re-await the very task that just
    /// failed and "fail" instantly forever.
    /// </summary>
    private void ForgetInFlight(string relPath)
    {
        lock (_gate) { _inFlight.Remove(relPath); }
    }

    public static long GetFreeBytes(string anyPathOnTargetDrive)
    {
        try
        {
            var rootPath = Path.GetPathRoot(Path.GetFullPath(anyPathOnTargetDrive));
            if (string.IsNullOrEmpty(rootPath)) return 0;
            return new DriveInfo(rootPath).AvailableFreeSpace;
        }
        catch { return 0; }
    }

    public async Task<SyncProgress> SyncAsync(IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var report = new SyncProgress();
        List<RemoteFile> manifest;
        try
        {
            report.Message = "Fetching manifest…";
            progress?.Report(Snapshot(report));
            manifest = await FetchManifestAsync(ct);
        }
        catch (Exception ex)
        {
            report.Message = $"Manifest fetch failed: {ex.Message}";
            report.Done = true;
            progress?.Report(Snapshot(report));
            return report;
        }

        var missing = manifest.Where(f => !IsCached(f.Path, f.Size)).ToList();
        report.FilesTotal = missing.Count;
        report.BytesNeeded = missing.Sum(f => f.Size);
        report.FreeBytes = GetFreeBytes(_cacheDir);
        report.Pending = missing
            .GroupBy(f => SyncProgress.GameOf(f.Path) is { Length: > 0 } g ? g : "(loose files)",
                     StringComparer.OrdinalIgnoreCase)
            .Select(g => new PendingGame(g.Key, g.Count(), g.Sum(f => f.Size)))
            .OrderByDescending(g => g.Files)
            .ToList();

        long budget = Math.Max(0, report.FreeBytes - DiskHeadroomBytes);
        long planned = 0;

        if (missing.Count == 0)
        {
            report.Message = "Already up to date.";
            report.Done = true;
            progress?.Report(Snapshot(report));
            return report;
        }

        report.Message = $"Need {missing.Count} files ({FormatBytes(report.BytesNeeded)}); free {FormatBytes(report.FreeBytes)}";
        progress?.Report(Snapshot(report));

        for (int i = 0; i < missing.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = missing[i];
            if (planned + f.Size > budget)
            {
                report.FilesSkipped++;
                continue;
            }
            report.CurrentFile = f.Path;
            var game = SyncProgress.GameOf(f.Path);
            report.Message = $"Downloading {i + 1}/{missing.Count}: " +
                             (string.IsNullOrEmpty(game) ? "" : $"{game} / ") + Path.GetFileName(f.Path);
            progress?.Report(Snapshot(report));

            // Retry the file, then move on. A single failure used to abandon the
            // entire remaining sync: restarting the server mid-sync severed one
            // download and stranded the other ~479 files until someone pushed
            // again. Over a DERP-relayed Tailscale link that is a routine event,
            // not an exceptional one.
            var downloaded = false;
            for (int attempt = 1; attempt <= MaxAttemptsPerFile && !downloaded; attempt++)
            {
                try
                {
                    await EnsureCachedAsync(f, ct);
                    downloaded = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Drop the faulted task so the retry actually re-downloads
                    // instead of awaiting the same failure again.
                    ForgetInFlight(f.Path);
                    if (attempt < MaxAttemptsPerFile)
                    {
                        report.Message = $"Retry {attempt}/{MaxAttemptsPerFile - 1} on {Path.GetFileName(f.Path)}: {ex.Message}";
                        progress?.Report(Snapshot(report));
                        await Task.Delay(RetryDelayMs * attempt, ct);
                    }
                    else
                    {
                        report.FilesFailed++;
                        report.LastError = $"{Path.GetFileName(f.Path)}: {ex.Message}";
                        Console.WriteLine($"Sync: giving up on {f.Path} after {MaxAttemptsPerFile} attempts ({ex.Message})");
                    }
                }
            }

            if (downloaded)
            {
                report.FilesDownloaded++;
                report.BytesDownloaded += f.Size;
                planned += f.Size;
            }
        }

        var parts = new List<string> { $"Synced {report.FilesDownloaded} files" };
        if (report.FilesSkipped > 0) parts.Add($"skipped {report.FilesSkipped} (insufficient disk space)");
        if (report.FilesFailed > 0) parts.Add($"{report.FilesFailed} failed");
        report.Message = string.Join("; ", parts) + ".";
        report.Done = true;
        progress?.Report(Snapshot(report));
        return report;
    }

    private static SyncProgress Snapshot(SyncProgress p) => new SyncProgress
    {
        FilesTotal = p.FilesTotal,
        FilesDownloaded = p.FilesDownloaded,
        FilesSkipped = p.FilesSkipped,
        FilesFailed = p.FilesFailed,
        LastError = p.LastError,
        BytesNeeded = p.BytesNeeded,
        BytesDownloaded = p.BytesDownloaded,
        FreeBytes = p.FreeBytes,
        CurrentFile = p.CurrentFile,
        Message = p.Message,
        Done = p.Done,
        Pending = p.Pending,
    };

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.##} {units[u]}";
    }

    private async Task<string> DownloadAsync(RemoteFile file, string local, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        var tmp = local + ".part";
        var url = $"{_baseUrl}/file/{Uri.EscapeDataString(file.Path).Replace("%2F", "/")}";
        Console.WriteLine($"Downloading {url} -> {local}");
        using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var input = await resp.Content.ReadAsStreamAsync(ct);
            await using (var output = File.Create(tmp))
            {
                await input.CopyToAsync(output, 81920, ct);
            }
        }
        if (File.Exists(local)) File.Delete(local);
        File.Move(tmp, local);
        return local;
    }

    private class ManifestDto
    {
        [JsonPropertyName("files")]
        public List<ManifestItemDto>? Files { get; set; }
    }

    private class ManifestItemDto
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;
        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
