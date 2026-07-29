using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pinscreen2.App;

/// <summary>A release asset this platform can download and run itself.</summary>
public record InstallerAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>Result of an update check.</summary>
public class UpdateInfo
{
    public Version? Latest { get; init; }
    public Version? Current { get; init; }
    public string Tag { get; init; } = "";
    public string ReleaseName { get; init; } = "";
    public string HtmlUrl { get; init; } = "";
    public string PublishedAt { get; init; } = "";
    public string Notes { get; init; } = "";

    /// <summary>Non-null when the app can install the update without a browser.</summary>
    public InstallerAsset? Installer { get; init; }

    public bool IsNewer => Latest != null && Current != null && Latest > Current;
    public bool CanSelfInstall => IsNewer && Installer != null;
}

/// <summary>
/// One-click update: ask GitHub for the latest release, download the Windows
/// installer ourselves, and run it silently over the top.
///
/// Downloading it here rather than sending the user to a browser matters for a
/// kiosk. These machines are wall-mounted, often without a keyboard, and the
/// browser route is a Save dialog, a SmartScreen warning (every release is a
/// fresh unsigned binary with no reputation), an install wizard, and a manual
/// relaunch. A file the app writes carries no Mark-of-the-Web, so the whole
/// update is one click plus the single unavoidable UAC consent.
/// </summary>
public static class UpdateService
{
    public const string GitHubRepo = "davidvanderburgh/pinscreen-2";

    // CI publishes Pinscreen2_Setup_v{X.Y.Z}_win-x64.exe -- see the workflow's
    // "Rename installer" step. Match on both ends so the app zips never match.
    private const string InstallerPrefix = "Pinscreen2_Setup";
    private const string InstallerSuffix = "_win-x64.exe";

    // Inno Setup switches for an unattended install-over-the-top:
    //   /SILENT                  progress window only, no wizard
    //   /NORESTART               never reboot a kiosk out from under itself
    //   /FORCECLOSEAPPLICATIONS  let Setup close us if we still hold files
    //   /RELAUNCH=1              custom flag pinscreen2.iss reads to restart
    //                            the app afterwards ([Run] is skipifsilent)
    private const string InstallerArgs = "/SILENT /NORESTART /FORCECLOSEAPPLICATIONS /RELAUNCH=1";

    private const int ChunkSize = 256 * 1024;

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Pinscreen2-UpdateCheck");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public static async Task<UpdateInfo> CheckAsync(Version? current, CancellationToken ct = default)
    {
        using var http = NewClient(TimeSpan.FromSeconds(15));
        using var resp = await http.GetAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest", ct);
        resp.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync(ct)).RootElement;
        var tag = doc.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
        var name = doc.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
        var html = doc.TryGetProperty("html_url", out var u)
            ? (u.GetString() ?? $"https://github.com/{GitHubRepo}/releases")
            : $"https://github.com/{GitHubRepo}/releases";
        var published = doc.TryGetProperty("published_at", out var p) ? (p.GetString() ?? "") : "";
        var notes = doc.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";

        return new UpdateInfo
        {
            Latest = ParseVersion(tag),
            Current = current,
            Tag = tag,
            ReleaseName = name,
            HtmlUrl = html,
            PublishedAt = published,
            Notes = notes,
            Installer = PickInstaller(doc),
        };
    }

    /// <summary>
    /// The Windows installer asset, or null. Returning null when the asset is
    /// missing also covers the window where the release row exists but CI is
    /// still uploading -- the button must never point at a download that isn't
    /// there yet.
    /// </summary>
    private static InstallerAsset? PickInstaller(JsonElement release)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var nm) ? (nm.GetString() ?? "") : "";
            var url = a.TryGetProperty("browser_download_url", out var du) ? (du.GetString() ?? "") : "";
            if (string.IsNullOrEmpty(url)) continue;
            if (!name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.EndsWith(InstallerSuffix, StringComparison.OrdinalIgnoreCase)) continue;

            long size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var s) ? s : 0;
            // GitHub publishes "sha256:<hex>" on newer uploads; absent on older ones.
            string? sha = null;
            if (a.TryGetProperty("digest", out var dg))
            {
                var d = dg.GetString() ?? "";
                if (d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = d["sha256:".Length..];
            }
            return new InstallerAsset(name, url, size, sha);
        }
        return null;
    }

    /// <summary>
    /// Streams the installer to a temp file and returns its path. A cancelled,
    /// short, or digest-mismatched download deletes the partial file and throws
    /// -- never leave a half-written exe somewhere the caller might run it.
    /// </summary>
    public static async Task<string> DownloadAsync(
        InstallerAsset asset, IProgress<(long done, long total)>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Pinscreen2Update");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, asset.Name);

        using var http = NewClient(TimeSpan.FromMinutes(30));
        try
        {
            using var resp = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? asset.Size;

            using var sha = SHA256.Create();
            await using (var input = await resp.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(dest))
            {
                var buffer = new byte[ChunkSize];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    done += read;
                    progress?.Report((done, total));
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            }

            if (!string.IsNullOrEmpty(asset.Sha256))
            {
                var actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Download failed integrity check (sha256 {actual[..12]}… != published {asset.Sha256[..12]}…).");
            }
            return dest;
        }
        catch
        {
            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Runs the downloaded installer unattended. UseShellExecute honours the
    /// setup exe's admin manifest, which raises the one UAC consent; the app
    /// should exit immediately after so Setup can replace its files.
    /// </summary>
    public static void LaunchInstaller(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            Arguments = InstallerArgs,
            UseShellExecute = true,
        });
    }

    public static Version? ParseVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var s = v.Trim().TrimStart('v', 'V');
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var ver) ? ver : null;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double val = bytes;
        int u = 0;
        while (val >= 1024 && u < units.Length - 1) { val /= 1024; u++; }
        return $"{val:0.#} {units[u]}";
    }
}
