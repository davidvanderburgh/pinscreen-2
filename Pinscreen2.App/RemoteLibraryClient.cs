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

public class RemoteLibraryClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _cacheDir;
    private readonly Dictionary<string, Task<string>> _inFlight = new();
    private readonly object _gate = new();

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
