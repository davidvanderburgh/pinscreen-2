using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Pinscreen2.Server;

string root = "";
int port = 8080;
bool portFromArgs = false;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--root" && i + 1 < args.Length) root = args[++i];
    else if (args[i] == "--port" && i + 1 < args.Length) { port = int.Parse(args[++i]); portFromArgs = true; }
}

// Directory the exe lives in -- for a single-file publish AppContext.BaseDirectory
// can point at an extraction dir, so prefer the real process path.
string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

// Own our logging so the scheduled task can launch the exe directly.
if (!args.Contains("--no-log")) ServerLog.Install(Path.Combine(exeDir, "server.log"));

var cfg = new ServerConfig();
var cfgPath = Path.Combine(exeDir, "server-config.json");
if (File.Exists(cfgPath))
{
    try
    {
        cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(cfgPath)) ?? new ServerConfig();
        // Command-line args win over the config file.
        if (string.IsNullOrWhiteSpace(root) && !string.IsNullOrWhiteSpace(cfg.Root)) root = cfg.Root;
        if (cfg.Port > 0 && !portFromArgs) port = cfg.Port;
    }
    catch (Exception ex) { Console.WriteLine($"Failed to read server-config.json: {ex.Message}"); }
}

if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
{
    Console.Error.WriteLine($"Root folder not found: '{root}'. Pass --root <path> or create server-config.json next to the exe.");
    return 1;
}

root = Path.GetFullPath(root);
Console.WriteLine($"Serving '{root}' on http://0.0.0.0:{port}");

var library = new LibraryService(root);
var devices = new DeviceRegistry(Path.Combine(exeDir, "devices.json"));
var releases = new ReleaseWatcher();

var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
// Framework request logging at Information level writes ~6 lines per HTTP call.
// A full sync is tens of thousands of file requests, which buried the useful
// lines and blew the log up. Our own Console.WriteLine output is unaffected.
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton(library);
builder.Services.AddSingleton(devices);
builder.Services.AddSingleton(releases);
var app = builder.Build();

var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

// Scan in the background rather than before app.Run(). A cold scan of a large
// library takes 30-45s, and blocking startup meant the server refused every
// connection for that whole window -- screens could not even download files
// they already knew about. Serving starts immediately; /manifest.json returns
// 503 until the first scan lands.
_ = Task.Run(() => library.Refresh());

// Rescan and, when the library actually changed and AutoPushOnChange is on,
// tell every connected pinscreen to sync. That is the whole point of the push
// channel: curate on this machine, screens update themselves.
bool RefreshAndMaybePush(string reason)
{
    var changed = library.Refresh();
    if (!changed) return false;

    if (cfg.AutoPushOnChange)
    {
        var n = devices.Broadcast("sync", new { reason, at = library.BuiltAt });
        if (n > 0) Console.WriteLine($"Library changed -- pushed sync to {n} device(s)");
    }
    else
    {
        // Auto-push off still means screens should re-diff themselves, so the
        // dashboard can show who is behind before anyone presses Sync.
        var n = devices.Broadcast("check", new { reason, at = library.BuiltAt });
        if (n > 0) Console.WriteLine($"Library changed -- asked {n} device(s) to re-check");
    }
    return true;
}

var refreshTimer = new System.Threading.Timer(_ => RefreshAndMaybePush("library-changed"), null,
    TimeSpan.FromMinutes(cfg.RefreshMinutes), TimeSpan.FromMinutes(cfg.RefreshMinutes));

// Which app version is available, so the dashboard can flag out-of-date screens.
_ = releases.RefreshAsync();
var releaseTimer = new System.Threading.Timer(_ => _ = releases.RefreshAsync(), null,
    TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));

// Notification-area icon. Skipped for headless/service runs, where there is no
// desktop to attach to.
TrayIcon? tray = null;
if (!args.Contains("--no-tray") && cfg.ShowTrayIcon && OperatingSystem.IsWindows())
{
    tray = new TrayIcon(library, devices, port, exeDir, RefreshAndMaybePush);
    tray.Start();
}

// ---------------------------------------------------------------- pinscreen API

app.MapGet("/manifest.json", (HttpContext ctx) =>
{
    var bytes = library.ManifestBytes;
    if (bytes == null) return Results.StatusCode(503);
    ctx.Response.Headers["X-Manifest-Built-At"] = library.BuiltAt.ToString("O");
    return Results.Bytes(bytes, "application/json");
});

app.MapPost("/manifest/refresh", () => { library.Refresh(); return Results.Ok(new { built = library.BuiltAt }); });

var contentTypes = new FileExtensionContentTypeProvider();
app.MapGet("/file/{**path}", (string path) =>
{
    var full = library.ResolveFile(Uri.UnescapeDataString(path));
    if (full == null) return Results.NotFound();
    if (!contentTypes.TryGetContentType(full, out var contentType)) contentType = "application/octet-stream";
    return Results.File(full, contentType, enableRangeProcessing: true);
});

// Server-Sent Events channel. Each pinscreen holds this open; the server writes
// command frames into it. A comment frame every 20s keeps NAT and Tailscale DERP
// relays from reaping an idle connection.
app.MapGet("/events", async (HttpContext ctx, string? deviceId, string? name, string? version) =>
{
    if (string.IsNullOrWhiteSpace(deviceId))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("deviceId is required");
        return;
    }

    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    devices.Touch(deviceId, name, version, ctx.Connection.RemoteIpAddress?.ToString());
    var conn = devices.OpenConnection(deviceId);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, conn.Cts.Token);
    var ct = linked.Token;

    try
    {
        await ctx.Response.WriteAsync($"retry: 5000\nevent: hello\ndata: {{\"deviceId\":\"{deviceId}\"}}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            string? frame = null;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                frame = await conn.Outbox.Reader.ReadAsync(wait.Token);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) break; // real shutdown, not the keepalive tick
            }
            catch (System.Threading.Channels.ChannelClosedException) { break; }

            await ctx.Response.WriteAsync(frame ?? ": keepalive\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex) { Console.Error.WriteLine($"SSE stream error for {deviceId}: {ex.Message}"); }
    finally { devices.CloseConnection(conn); }
});

app.MapPost("/api/devices/{id}/status", async (string id, HttpContext ctx) =>
{
    var dto = await JsonSerializer.DeserializeAsync<DeviceStatusDto>(ctx.Request.Body, jsonOpts);
    if (dto == null) return Results.BadRequest();
    devices.ApplyStatus(id, dto, ctx.Connection.RemoteIpAddress?.ToString());
    return Results.Ok();
});

// ---------------------------------------------------------------- dashboard API

app.MapGet("/api/stats", () => Results.Json(new
{
    root = library.Root,
    ready = library.IsReady,
    files = library.FileCount,
    bytes = library.TotalBytes,
    games = library.Games().Count,
    builtAt = library.BuiltAt,
    devicesOnline = devices.OnlineCount,
    autoPushOnChange = cfg.AutoPushOnChange,
}, jsonOpts));

app.MapGet("/api/library", () => Results.Json(library.Games(), jsonOpts));

app.MapGet("/api/library/{game}", (string game) => Results.Json(library.GameFiles(Uri.UnescapeDataString(game)), jsonOpts));

app.MapGet("/api/devices", () =>
{
    var list = devices.Snapshot();
    foreach (var d in list) d.UpdateAvailable = releases.IsOutOfDate(d.Version);
    return Results.Json(list, jsonOpts);
});

app.MapGet("/api/release", async () =>
{
    // Refresh on read when stale. The background timer alone left the dashboard
    // advertising a version two releases behind.
    await releases.EnsureFreshAsync(TimeSpan.FromMinutes(5));
    return Results.Json(releases.Current, jsonOpts);
});

app.MapPost("/api/release/refresh", async () =>
{
    await releases.RefreshAsync();
    return Results.Json(releases.Current, jsonOpts);
});

// Tell screens to update themselves: they check GitHub, download the installer,
// verify it, and run it silently. Only reaches screens that are online.
app.MapPost("/api/devices/{id}/update", (string id) =>
{
    var sent = devices.Send(id, "update", new { reason = "manual", at = DateTimeOffset.UtcNow });
    return sent ? Results.Ok(new { sent = 1 }) : Results.Json(new { sent = 0, error = "device offline" }, statusCode: 409);
});

app.MapPost("/api/devices/update-all", () =>
{
    var n = devices.Broadcast("update", new { reason = "manual-all", at = DateTimeOffset.UtcNow });
    return Results.Ok(new { sent = n });
});

app.MapDelete("/api/devices/{id}", (string id) =>
    devices.Forget(id) ? Results.Ok() : Results.NotFound());

app.MapPost("/api/devices/{id}/name", async (string id, HttpContext ctx) =>
{
    var dto = await JsonSerializer.DeserializeAsync<RenameDto>(ctx.Request.Body, jsonOpts);
    var name = (dto?.Name ?? "").Trim();
    if (name.Length > 60) name = name[..60];

    var rec = devices.Rename(id, name);
    if (rec == null) return Results.NotFound();

    // Push it to the screen so it persists the name in its own config; the
    // server-side override covers the case where it is offline right now.
    devices.Send(id, "rename", new { name = rec.Name });
    Console.WriteLine($"Device {id} renamed to '{rec.Name}'");
    return Results.Ok(new { name = rec.Name });
});

app.MapPost("/api/devices/{id}/sync", (string id) =>
{
    var sent = devices.Send(id, "sync", new { reason = "manual", at = DateTimeOffset.UtcNow });
    return sent ? Results.Ok(new { sent = 1 }) : Results.Json(new { sent = 0, error = "device offline" }, statusCode: 409);
});

app.MapPost("/api/devices/sync-all", () =>
{
    var n = devices.Broadcast("sync", new { reason = "manual-all", at = DateTimeOffset.UtcNow });
    return Results.Ok(new { sent = n });
});

// Ask screens to re-diff themselves against the library without downloading.
app.MapPost("/api/devices/check-all", () =>
{
    var n = devices.Broadcast("check", new { reason = "manual-all", at = DateTimeOffset.UtcNow });
    return Results.Ok(new { sent = n });
});

app.MapPost("/api/devices/{id}/check", (string id) =>
{
    var sent = devices.Send(id, "check", new { reason = "manual", at = DateTimeOffset.UtcNow });
    return sent ? Results.Ok(new { sent = 1 }) : Results.Json(new { sent = 0, error = "device offline" }, statusCode: 409);
});

app.MapPost("/api/manifest/refresh", () =>
{
    var changed = RefreshAndMaybePush("rescan");
    return Results.Ok(new { built = library.BuiltAt, changed, files = library.FileCount });
});

// ---------------------------------------------------------------- dashboard UI

// Embedded so the self-contained single-file publish stays a single file.
app.MapGet("/", () =>
{
    var asm = Assembly.GetExecutingAssembly();
    using var s = asm.GetManifestResourceStream("Pinscreen2.Server.index.html");
    if (s == null) return Results.NotFound("Dashboard resource missing.");
    using var r = new StreamReader(s);
    return Results.Content(r.ReadToEnd(), "text/html; charset=utf-8");
});

// Also what a browser app-mode window uses for its taskbar icon.
byte[]? faviconBytes = null;
app.MapGet("/favicon.ico", () =>
{
    if (faviconBytes == null)
    {
        using var s = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Pinscreen2.Server.favicon.ico");
        if (s == null) return Results.NotFound();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        faviconBytes = ms.ToArray();
    }
    return Results.Bytes(faviconBytes, "image/x-icon");
});

app.Run();
tray?.Dispose();
GC.KeepAlive(refreshTimer);
GC.KeepAlive(releaseTimer);
return 0;
