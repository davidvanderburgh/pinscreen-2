using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

string root = "";
int port = 8080;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--root" && i + 1 < args.Length) root = args[++i];
    else if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
}

if (string.IsNullOrWhiteSpace(root))
{
    var cfgPath = Path.Combine(AppContext.BaseDirectory, "server-config.json");
    if (File.Exists(cfgPath))
    {
        try
        {
            var cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(cfgPath));
            if (cfg != null)
            {
                if (string.IsNullOrWhiteSpace(root) && !string.IsNullOrWhiteSpace(cfg.Root)) root = cfg.Root;
                if (cfg.Port > 0) port = cfg.Port;
            }
        }
        catch (Exception ex) { Console.WriteLine($"Failed to read server-config.json: {ex.Message}"); }
    }
}

if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
{
    Console.Error.WriteLine($"Root folder not found: '{root}'. Pass --root <path> or create server-config.json next to the exe.");
    return 1;
}

root = Path.GetFullPath(root);
Console.WriteLine($"Serving '{root}' on http://0.0.0.0:{port}");

var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
var app = builder.Build();

string[] videoExts = { ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".webm" };

// Cache the manifest in memory. Walking 30k+ files takes ~5 seconds per
// request -- that's a long window for a flaky Tailscale DERP-relayed link
// to time out, plus it pegs the CPU on every sync attempt. Build once,
// serve from cache, refresh on a timer in the background.
var manifestLock = new object();
byte[]? manifestBytes = null;
DateTimeOffset manifestBuiltAt = DateTimeOffset.MinValue;

byte[] BuildManifest()
{
    var items = new List<ManifestItem>();
    var stack = new Stack<string>();
    stack.Push(root);
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
                if (Array.IndexOf(videoExts, ext) < 0) continue;
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                long size = 0;
                try { size = new FileInfo(f).Length; } catch { }
                items.Add(new ManifestItem(rel, size));
            }
        }
        catch { }
    }
    return JsonSerializer.SerializeToUtf8Bytes(new Manifest(items));
}

void RefreshManifest()
{
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bytes = BuildManifest();
        sw.Stop();
        lock (manifestLock)
        {
            manifestBytes = bytes;
            manifestBuiltAt = DateTimeOffset.UtcNow;
        }
        Console.WriteLine($"Manifest refreshed: {bytes.Length} bytes in {sw.ElapsedMilliseconds} ms");
    }
    catch (Exception ex) { Console.Error.WriteLine($"Manifest refresh failed: {ex.Message}"); }
}

// Build once at startup so the first sync request is instant.
RefreshManifest();
// Refresh every 5 minutes in the background; new videos appear after the
// next refresh tick. POST /manifest/refresh forces an immediate rebuild.
var refreshTimer = new System.Threading.Timer(_ => RefreshManifest(), null,
    TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

app.MapGet("/manifest.json", (HttpContext ctx) =>
{
    byte[]? bytes;
    DateTimeOffset builtAt;
    lock (manifestLock) { bytes = manifestBytes; builtAt = manifestBuiltAt; }
    if (bytes == null) return Results.StatusCode(503);
    ctx.Response.Headers["X-Manifest-Built-At"] = builtAt.ToString("O");
    return Results.Bytes(bytes, "application/json");
});

app.MapPost("/manifest/refresh", () => { RefreshManifest(); return Results.Ok(new { built = manifestBuiltAt }); });

var contentTypes = new FileExtensionContentTypeProvider();
app.MapGet("/file/{**path}", (string path, HttpContext ctx) =>
{
    var decoded = Uri.UnescapeDataString(path).Replace('/', Path.DirectorySeparatorChar);
    var full = Path.GetFullPath(Path.Combine(root, decoded));
    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
    if (!File.Exists(full)) return Results.NotFound();
    if (!contentTypes.TryGetContentType(full, out var contentType)) contentType = "application/octet-stream";
    return Results.File(full, contentType, enableRangeProcessing: true);
});

app.Run();
return 0;

record ManifestItem(string Path, long Size);
record Manifest(List<ManifestItem> Files);
class ServerConfig { public string Root { get; set; } = ""; public int Port { get; set; } = 8080; }
