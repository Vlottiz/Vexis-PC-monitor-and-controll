using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

/// <summary>
/// Checks GitHub Releases for a newer Vexis and installs it in one click:
/// downloads the release's setup .exe, runs it silently with /UPDATE (the
/// installer closes Vexis, replaces the files and starts the new version).
/// </summary>
public static class UpdateManager
{
    public const string Repo       = "Vlottiz/Vexis-PC-monitor-and-controll";
    public const string ReleasesUrl = "https://github.com/" + Repo + "/releases/latest";
    private const string ApiLatest = "https://api.github.com/repos/" + Repo + "/releases/latest";

    public record ReleaseInfo(string Tag, Version Version, string? InstallerUrl, long InstallerSize, string Notes,
                              string? InstallerName = null, string? ChecksumUrl = null);

    public static ReleaseInfo? Latest { get; private set; }
    public static bool Busy { get; private set; }

    private static HttpClient NewClient(string appVersion, TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.Add("User-Agent", $"Vexis/{appVersion}");
        return http;
    }

    /// <summary>Returns the latest release, or null if there is none / it can't be reached.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(string appVersion)
    {
        using var http = NewClient(appVersion, TimeSpan.FromSeconds(10));
        using var resp = await http.GetAsync(ApiLatest);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Console.WriteLine("[update] No GitHub releases published yet (or the repository is private).");
            return null;
        }
        resp.EnsureSuccessStatusCode();
        Latest = ParseRelease(await resp.Content.ReadAsStringAsync());
        return Latest;
    }

    /// <summary>
    /// Reads a GitHub "latest release" response: the version from the tag (v2.11.0 or 2.11.0)
    /// and the installer asset (a *Setup*.exe first, otherwise any .exe). Null when the tag
    /// isn't a version number, which is why tags like "Vexis" never offer an update.
    /// </summary>
    internal static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;

        // Prefer the setup file build-installer.bat makes; otherwise any .exe asset
        string? url = null, installer = null; long size = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray()
                         .OrderByDescending(a => (a.GetProperty("name").GetString() ?? "").Contains("Setup", StringComparison.OrdinalIgnoreCase)))
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                url  = a.GetProperty("browser_download_url").GetString();
                size = a.GetProperty("size").GetInt64();
                installer = name;
                break;
            }
        }
        // The release workflow publishes "<installer>.sha256" next to the installer
        string? sumUrl = null;
        if (installer != null && root.TryGetProperty("assets", out var assets2))
            foreach (var a in assets2.EnumerateArray())
                if (string.Equals(a.GetProperty("name").GetString(), installer + ".sha256", StringComparison.OrdinalIgnoreCase))
                { sumUrl = a.GetProperty("browser_download_url").GetString(); break; }
        string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        return new ReleaseInfo(tag, ver, url, size, notes, installer, sumUrl);
    }

    /// <summary>
    /// The SHA-256 for <paramref name="fileName"/> from a checksum file: either a bare hash, or
    /// "hash  name" lines (sha256sum format). Null if there isn't one.
    /// </summary>
    internal static string? ParseChecksum(string text, string? fileName)
    {
        foreach (var raw in text.Split('\n'))
        {
            var parts = raw.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit)) continue;
            string name = parts.Length > 1 ? parts[1].TrimStart('*').Trim() : "";
            if (name == "" || fileName == null || string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    public static bool IsNewer(ReleaseInfo r, string appVersion) =>
        Version.TryParse(appVersion, out var cur) && r.Version > cur;

    /// <summary>
    /// Downloads the installer (reporting 0-100 %) and starts it. Returns true when
    /// the installer was launched — the caller should then exit the app.
    /// </summary>
    public static async Task<bool> DownloadAndRunAsync(ReleaseInfo r, string appVersion, Action<int, string> progress, bool restartMinimized = false)
    {
        if (Busy || r.InstallerUrl == null) return false;
        Busy = true;
        try
        {
            string dir  = Path.Combine(Path.GetTempPath(), "Vexis-Update");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"VexisHM-Setup-{r.Tag}.exe");

            progress(0, "Downloading " + r.Tag);
            using (var http = NewClient(appVersion, TimeSpan.FromMinutes(10)))
            using (var resp = await http.GetAsync(r.InstallerUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? r.InstallerSize;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(file);
                var buf = new byte[81920];
                long done = 0; int lastPct = -1, n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    int pct = total > 0 ? (int)(done * 100 / total) : 0;
                    if (pct != lastPct) { lastPct = pct; progress(pct, $"Downloading {r.Tag} — {done / 1048576.0:F1} MB"); }
                }
            }

            // Sanity checks: complete download and a Windows executable
            var info = new FileInfo(file);
            if (r.InstallerSize > 0 && info.Length != r.InstallerSize)
                throw new Exception($"download incomplete ({info.Length} of {r.InstallerSize} bytes)");
            using (var fs = File.OpenRead(file))
                if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
                    throw new Exception("downloaded file is not an installer");

            // Checksum: releases built by the GitHub workflow publish one. If it's there, the
            // installer must match it exactly or it is deleted instead of run.
            if (r.ChecksumUrl != null)
            {
                progress(100, "Checking the download");
                using var http = NewClient(appVersion, TimeSpan.FromSeconds(30));
                string? expected = ParseChecksum(await http.GetStringAsync(r.ChecksumUrl), r.InstallerName);
                if (expected == null) throw new Exception("the release's checksum file couldn't be read");
                string actual;
                using (var fs = File.OpenRead(file))
                    actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs)).ToLowerInvariant();
                if (actual != expected)
                {
                    try { File.Delete(file); } catch { }
                    throw new Exception("the download doesn't match the release's SHA-256 checksum, so it wasn't installed");
                }
                Console.WriteLine($"[update] SHA-256 verified: {actual}");
            }
            else Console.WriteLine("[update] This release has no checksum file; size and file type were checked.");

            progress(100, "Installing — Vexis will restart");
            string args = "/S /UPDATE" + (restartMinimized ? " /MINIMIZED" : "");
            Console.WriteLine($"[update] Running {file} {args}");
            Process.Start(new ProcessStartInfo { FileName = file, Arguments = args, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[update] Update failed: {ex.Message}");
            progress(-1, "Update failed: " + ex.Message);
            return false;
        }
        finally { Busy = false; }
    }
}
