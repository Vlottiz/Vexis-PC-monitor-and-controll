using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;
using System.Text.Json.Serialization;

// Factory reset, handing fans back, closing for the installer, window placement
public partial class MainForm
{
    // ── Reset to factory settings ──────────────────────────────────────────────
    // Back to a fresh install: fans to BIOS/driver control, no startup entry, saved
    // settings and page storage deleted, then Vexis restarts. CSV recordings are kept.
    private bool _resetting;
    private async void FactoryReset()
    {
        if (_resetting) return;
        _resetting = true;
        Console.WriteLine("[reset] Reset to factory settings");
        try { _fans.RestoreAll(); } catch (Exception ex) { Console.WriteLine($"[reset] fans: {ex.Message}"); }
        try { if (_recorder.IsRecording) _recorder.Stop(); } catch (Exception ex) { Console.WriteLine($"[csv] Couldn't stop the recording cleanly: {ex.Message}"); }
        try { ClearInbox(); } catch { }   // nothing left to clear
        try { await Task.Run(() => StartupManager.SetEnabled(false)); StartWithWindowsEnabled = false; }
        catch (Exception ex) { Console.WriteLine($"[reset] startup: {ex.Message}"); }
        AppConfig.DeleteSaved();
        // Page storage (theme cache, text sizes, RGB brightness, menu order…) — every page shares it
        try
        {
            if (_webView.CoreWebView2 != null)
                await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                    Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.LocalStorage |
                    Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.IndexedDb |
                    Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.CacheStorage);
        }
        catch (Exception ex) { Console.WriteLine($"[reset] page storage: {ex.Message}"); }

        // Start a fresh copy that waits for this one to close, then quit
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = Environment.ProcessPath ?? Application.ExecutablePath,
                Arguments       = $"--after-reset {Environment.ProcessId}",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { Console.WriteLine($"[reset] relaunch: {ex.Message}"); }
        _tray.Visible = false;
        Application.Exit();
    }

    private int _fansRestored;
    private void RestoreFansSafely(string why)
    {
        if (Interlocked.Exchange(ref _fansRestored, 1) == 1) return;   // once is enough
        try { _fans?.RestoreAll(); Console.WriteLine($"[fans] Handed back on {why}"); }
        catch (Exception ex) { Console.WriteLine($"[fans] Hand-back on {why} failed: {ex.Message}"); }
    }

    // ── Closing for the installer ──────────────────────────────────────────────
    // The installer writes this file and gives Vexis a few seconds to close by itself
    // (fans handed back, recording closed, no "didn't close properly" banner next time)
    // before it force-closes anything still running.
    private static string ExitRequestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vexis", "exit.request");
    private int _exitCheck;

    private void ExitForInstaller()
    {
        try { File.Delete(ExitRequestPath); } catch { }   // not there: nothing to remove
        Console.WriteLine("[exit] Closing for the installer");
        _tray.Visible = false;
        Application.Exit();
    }

    private void OnClosing(object? s, FormClosingEventArgs e)
    {
        SavePlacement();
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
    }

    // ── Window size/position, remembered between launches ──────────────────────
    private void RestorePlacement()
    {
        var w = _config.Window;
        if (w == null || w.Width < MinimumSize.Width || w.Height < MinimumSize.Height) return;
        var rect = new Rectangle(w.X, w.Y, w.Width, w.Height);
        // Only restore if a decent part of the window lands on a connected screen
        bool visible = Screen.AllScreens.Any(sc =>
        {
            var overlap = Rectangle.Intersect(sc.WorkingArea, rect);
            return overlap.Width >= 150 && overlap.Height >= 80;
        });
        if (!visible) { Console.WriteLine("[window] Saved position is off-screen — using default."); return; }
        Bounds = rect;
        if (w.Maximized) WindowState = FormWindowState.Maximized;
    }

    private void SavePlacement()
    {
        if (_config == null || _isFullscreen || !IsHandleCreated) return;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (b.Width < MinimumSize.Width || b.Height < MinimumSize.Height) return;
        _config.Window = new WindowPlacement
        {
            X = b.X, Y = b.Y, Width = b.Width, Height = b.Height,
            Maximized = WindowState == FormWindowState.Maximized
        };
        _config.Save();
    }
}
