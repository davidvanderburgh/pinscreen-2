using System.Text.Json;

namespace Pinscreen2.Server;

/// <summary>
/// Tracks the latest published app release so the dashboard can say which
/// screens are out of date without each screen having to ask GitHub first.
///
/// Polled on a slow timer and cached. Failure is normal and non-fatal: a server
/// with no internet still serves videos and pushes syncs perfectly well, it
/// just cannot advise on updates.
/// </summary>
public class ReleaseWatcher
{
    private const string Repo = "davidvanderburgh/pinscreen-2";
    private const string InstallerPrefix = "Pinscreen2_Setup";
    private const string InstallerSuffix = "_win-x64.exe";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly object _gate = new();

    private string _latestTag = "";
    private Version? _latestVersion;
    private bool _hasInstaller;
    private DateTimeOffset? _checkedAt;
    private string _error = "";

    public ReleaseWatcher()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Pinscreen2-Server");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public record Snapshot(string Tag, string? Version, bool HasInstaller, DateTimeOffset? CheckedAt, string Error);

    public Snapshot Current
    {
        get
        {
            lock (_gate)
                return new Snapshot(_latestTag, _latestVersion?.ToString(), _hasInstaller, _checkedAt, _error);
        }
    }

    /// <summary>Latest version, or null when unknown. Used to flag out-of-date screens.</summary>
    public Version? LatestVersion { get { lock (_gate) return _latestVersion; } }

    public async Task RefreshAsync()
    {
        try
        {
            using var resp = await _http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            resp.EnsureSuccessStatusCode();
            var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync()).RootElement;

            var tag = doc.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            var hasInstaller = false;
            if (doc.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var nm) ? (nm.GetString() ?? "") : "";
                    if (name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(InstallerSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        hasInstaller = true;
                        break;
                    }
                }
            }

            lock (_gate)
            {
                _latestTag = tag;
                _latestVersion = ParseVersion(tag);
                _hasInstaller = hasInstaller;
                _checkedAt = DateTimeOffset.UtcNow;
                _error = "";
            }
            Console.WriteLine($"Latest release: {tag}{(hasInstaller ? "" : " (installer not uploaded yet)")}");
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _checkedAt = DateTimeOffset.UtcNow;
                _error = ex.Message;
            }
            Console.WriteLine($"Release check failed: {ex.Message}");
        }
    }

    public static Version? ParseVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var s = v.Trim().TrimStart('v', 'V');
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var parsed) ? parsed : null;
    }

    /// <summary>True when we know a newer release exists than what this screen reports.</summary>
    public bool IsOutOfDate(string? deviceVersion)
    {
        var latest = LatestVersion;
        if (latest == null) return false;
        var current = ParseVersion(deviceVersion);
        return current != null && latest > current;
    }
}
