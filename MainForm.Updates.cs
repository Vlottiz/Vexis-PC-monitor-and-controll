using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;
using System.Text.Json.Serialization;

// Update check and install (GitHub Releases, see UpdateManager)
public partial class MainForm
{
    // ── Updates (GitHub Releases) ──────────────────────────────────────────────
    private string? _updateJson;       // last check result, re-sent to pages that load later
    private bool    _autoUpdateTried;  // auto-install at most once per launch
    private int     _updateChecking;

    private void PushUpdateInfo(bool force)
    {
        if (!force && _updateJson != null)
        {
            RunScriptEverywhere($"typeof navUpdateInfo==='function'&&navUpdateInfo({_updateJson})");
            return;
        }
        if (Interlocked.Exchange(ref _updateChecking, 1) == 1) return; // one check at a time
        _ = Task.Run(async () => { try { await CheckForUpdateAsync(); } finally { _updateChecking = 0; } });
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var r = await UpdateManager.CheckAsync(AppVersionText);
            bool newer = r != null && UpdateManager.IsNewer(r, AppVersionText);
            if (r != null)
                Console.WriteLine(newer ? $"[update] New version available: {r.Tag}"
                                        : $"[update] Up to date (running v{AppVersionText}, latest {r.Tag})");
            var info = new
            {
                current  = AppVersionText,
                latest   = r?.Tag,
                available = newer,
                canInstall = newer && r!.InstallerUrl != null && r.ChecksumUrl != null,   // no checksum = no one-click install
                notes    = r?.Notes is { Length: > 1200 } n ? n[..1200] + "…" : r?.Notes,
                url      = UpdateManager.ReleasesUrl
            };
            _updateJson = JsonSerializer.Serialize(info, _json);
            RunScriptEverywhere($"typeof navUpdateInfo==='function'&&navUpdateInfo({_updateJson})");

            // Optional: install straight away (Settings → Install updates automatically)
            if (newer && r!.InstallerUrl != null && r.ChecksumUrl != null && !_autoUpdateTried &&
                _config.Settings?.GetValueOrDefault("autoUpdate") == "true")
            {
                _autoUpdateTried = true;
                Console.WriteLine($"[update] Auto-install: {r.Tag}");
                BeginInvoke(() => QueueNotification("Updating Vexis", $"Installing {r.Tag} — Vexis will restart in a moment.", ToolTipIcon.Info));
                await Task.Delay(3000);   // let the notification show first
                await InstallUpdateAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[update] Check failed: {ex.Message}");
            RunScriptEverywhere("typeof navUpdateInfo==='function'&&navUpdateInfo({offline:true})");
        }
    }

    private async Task InstallUpdateAsync()
    {
        var r = UpdateManager.Latest;
        if (r == null || !UpdateManager.IsNewer(r, AppVersionText) || r.InstallerUrl == null) return;
        void Report(int pct, string text) => RunScriptEverywhere(
            $"typeof navUpdateProgress==='function'&&navUpdateProgress({pct},{JsonSerializer.Serialize(text)})");
        bool inTray = Invoke(() => !Visible || Opacity == 0);   // restart the same way it was running
        if (await UpdateManager.DownloadAndRunAsync(r, AppVersionText, Report, restartMinimized: inTray))
        {
            // The installer closes Vexis anyway — exit cleanly first (fans back to BIOS/driver, CSV closed)
            await Task.Delay(800);
            Invoke(() => { _tray.Visible = false; Application.Exit(); });
        }
    }
}
