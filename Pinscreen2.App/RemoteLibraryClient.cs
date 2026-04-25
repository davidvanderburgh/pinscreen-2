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

public class SyncProgress
{
    public int FilesTotal { get; set; }
    public int FilesDownloaded { get; set; }
    public int FilesSkipped { get; set; }
    public long BytesNeeded { get; set; }
    public long BytesDownloaded { get; set; }
    public long FreeBytes { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public string? Message { get; set; }
    public bool Done { get; set; }
}

public class RemoteLibraryClient
{
    // Keep some breathing room on the destination drive after syncing.
    private const long DiskHeadroomBytes = 1024L * 1024 * 1024; // 1 GB

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
            report.Message = $"Downloading {i + 1}/{missing.Count}: {Path.GetFileName(f.Path)}";
            progress?.Report(Snapshot(report));
            try
            {
                await EnsureCachedAsync(f, ct);
                report.FilesDownloaded++;
                report.BytesDownloaded += f.Size;
                planned += f.Size;
            }
            catch (Exception ex)
            {
                report.Message = $"Failed on {Path.GetFileName(f.Path)}: {ex.Message}";
                report.Done = true;
                progress?.Report(Snapshot(report));
                return report;
            }
        }

        if (report.FilesSkipped > 0)
            report.Message = $"Synced {report.FilesDownloaded}; skipped {report.FilesSkipped} (insufficient disk space).";
        else
            report.Message = $"Synced {report.FilesDownloaded} files.";
        report.Done = true;
        progress?.Report(Snapshot(report));
        return report;
    }

    private static SyncProgress Snapshot(SyncProgress p) => new SyncProgress
    {
        FilesTotal = p.FilesTotal,
        FilesDownloaded = p.FilesDownloaded,
        FilesSkipped = p.FilesSkipped,
        BytesNeeded = p.BytesNeeded,
        BytesDownloaded = p.BytesDownloaded,
        FreeBytes = p.FreeBytes,
        CurrentFile = p.CurrentFile,
        Message = p.Message,
        Done = p.Done,
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
