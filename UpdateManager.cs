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
    /// and the installer asset (VexisHM-Setup.exe, optionally with a version: never any other
    /// .exe, since whatever is picked runs as administrator). Null when the tag
    /// isn't a version number, which is why tags like "Vexis" never offer an update.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex InstallerFile =
        new(@"^VexisHM-Setup(-v?[0-9.]+)?\.exe$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;

        // Only the installer the release workflow / build-installer.bat makes
        string? url = null, installer = null; long size = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (!InstallerFile.IsMatch(name)) continue;
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
        // Every release built by the GitHub workflow publishes a checksum. Without one there's
        // nothing to check the download against, so it isn't installed automatically.
        if (r.ChecksumUrl == null)
        {
            Console.WriteLine($"[update] {r.Tag} has no SHA-256 checksum — not installing automatically");
            progress(-1, "This release has no checksum to check it against. Download it from the release page instead.");
            return false;
        }
        Busy = true;
        try
        {
            // Folders left by earlier updates (the installer can't delete the folder it runs from)
            // Only folders this code makes ("Vexis-Update-" + 32 hex digits), nothing else in %TEMP%
            foreach (var old in Directory.EnumerateDirectories(Path.GetTempPath(), "Vexis-Update-*"))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(old), "^Vexis-Update-[0-9a-f]{32}$")) continue;
                try { Directory.Delete(old, true); }
                catch (Exception ex) { Console.WriteLine($"[update] Couldn't remove old {old}: {ex.Message}"); }
            }

            // A new folder with a random name for each update, and a file that must not exist yet,
            // so nothing planted in %TEMP% beforehand can be used.
            string dir  = Path.Combine(Path.GetTempPath(), "Vexis-Update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"VexisHM-Setup-{r.Tag}.exe");

            // Fetch the expected checksum first, so a release with a broken checksum file
            // isn't downloaded for nothing
            string? expected;
            using (var http = NewClient(appVersion, TimeSpan.FromSeconds(30)))
                expected = ParseChecksum(await http.GetStringAsync(r.ChecksumUrl), r.InstallerName);
            if (expected == null) throw new Exception("the release's checksum file couldn't be read");

            progress(0, "Downloading " + r.Tag);
            using (var http = NewClient(appVersion, TimeSpan.FromMinutes(10)))
            using (var resp = await http.GetAsync(r.InstallerUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? r.InstallerSize;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
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

            // Re-open the file read-only and keep it open until the installer has started.
            // While this handle is open, nothing else can write to, rename or delete the file,
            // so the bytes that are checked below are exactly the bytes that get run.
            // (Other programs can still read it, which the installer itself needs to.)
            progress(100, "Checking the download");
            using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // Sanity checks: complete download and a Windows executable
                if (r.InstallerSize > 0 && held.Length != r.InstallerSize)
                    throw new Exception($"download incomplete ({held.Length} of {r.InstallerSize} bytes)");
                if (held.ReadByte() != 'M' || held.ReadByte() != 'Z')
                    throw new Exception("downloaded file is not an installer");

                // The installer must match the release's SHA-256 exactly, or it isn't run
                held.Position = 0;
                string actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(held)).ToLowerInvariant();
                if (actual != expected)
                    throw new Exception("the download doesn't match the release's SHA-256 checksum, so it wasn't installed");
                Console.WriteLine($"[update] SHA-256 verified: {actual}");

                progress(100, "Installing — Vexis will restart");
                string args = "/S /UPDATE" + (restartMinimized ? " /MINIMIZED" : "");
                Console.WriteLine($"[update] Running {file} {args}");
                Process.Start(new ProcessStartInfo { FileName = file, Arguments = args, UseShellExecute = true });
            }
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
