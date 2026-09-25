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

    public record ReleaseInfo(string Tag, Version Version, string? InstallerUrl, long InstallerSize, string Notes);

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
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;

        // Prefer the setup file build-installer.bat makes; otherwise any .exe asset
        string? url = null; long size = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray()
                         .OrderByDescending(a => (a.GetProperty("name").GetString() ?? "").Contains("Setup", StringComparison.OrdinalIgnoreCase)))
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                url  = a.GetProperty("browser_download_url").GetString();
                size = a.GetProperty("size").GetInt64();
                break;
            }
        }
        string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        Latest = new ReleaseInfo(tag, ver, url, size, notes);
        return Latest;
    }

    public static bool IsNewer(ReleaseInfo r, string appVersion) =>
        Version.TryParse(appVersion, out var cur) && r.Version > cur;

    /// <summary>
    /// Downloads the installer (reporting 0-100 %) and starts it. Returns true when
    /// the installer was launched — the caller should then exit the app.
    /// </summary>
    public static async Task<bool> DownloadAndRunAsync(ReleaseInfo r, string appVersion, Action<int, string> progress)
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

            progress(100, "Installing — Vexis will restart");
            Console.WriteLine($"[update] Running {file} /S /UPDATE");
            Process.Start(new ProcessStartInfo { FileName = file, Arguments = "/S /UPDATE", UseShellExecute = true });
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
