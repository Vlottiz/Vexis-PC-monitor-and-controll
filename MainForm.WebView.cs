using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;
using System.Text.Json.Serialization;

// The interface: WebView2 setup and crash recovery, sending data and settings to pages, pop-out windows
public partial class MainForm
{
    // ── Interface (WebView2) ───────────────────────────────────────────────────
    // Built once on demand (at launch, or on first open after starting in the tray) and
    // rebuilt automatically if WebView2's browser process dies.
    private Task? _uiTask;
    private bool  _uiReady;
    private int   _uiRebuilds;
    private DateTime _uiRebuildWindow = DateTime.MinValue;

    private Task StartUiAsync() => _uiTask ??= InitWebViewAsync();

    private async Task InitWebViewAsync()
    {
        var webViewOpts = WebViewSecurity.EnvironmentOptions();

        // Creating the environment can fail briefly (runtime updating, profile still in use
        // by a closing instance) — retry a few times before giving up.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var webViewEnv = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Vexis", "WebView2Cache"),
                    options: webViewOpts);
                await _webView.EnsureCoreWebView2Async(webViewEnv);
                break;
            }
            catch (Exception ex) when (attempt < 5)
            {
                Console.WriteLine($"[webview] Start attempt {attempt} failed: {ex.Message} — retrying");
                await Task.Delay(1500 * attempt);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[webview] Could not start the interface: {ex}");
                _uiTask = null;   // let the next open try again
                await RevealWindow();
                return;
            }
        }

        // Page zoom (− / + buttons, Ctrl+scroll): restore last level, remember changes
        _webView.ZoomFactor = ZoomHelper.Parse(_config.Settings);
        _webView.ZoomFactorChanged += (_, _) =>
        {
            _config.Settings ??= new Dictionary<string, string>();
            _config.Settings["zoom"] = _webView.ZoomFactor.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            _config.Save();
            _webView.CoreWebView2?.ExecuteScriptAsync(ZoomHelper.Script(_webView.ZoomFactor));
        };

        // F11 = fullscreen. Caught here because the browser would otherwise swallow the key
        _webView.PreviewKeyDown += (s, e) =>
        {
            if (e.KeyCode == System.Windows.Forms.Keys.F11)
            {
                e.IsInputKey = true;
                ToggleFullscreen();
            }
        };

        // Clear WebView2's cached page files so pages from a new version always load fresh
        // (otherwise an update can show the old HTML/JS until the cache expires).
        // Page storage (settings, menu order) lives elsewhere and is kept.
        try
        {
            string wv2Root = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Vexis", "WebView2Cache", "EBWebView", "Default");
            foreach (var subdir in new[]{"Cache","Code Cache","GPUCache"})
            {
                string cacheDir = System.IO.Path.Combine(wv2Root, subdir);
                if (System.IO.Directory.Exists(cacheDir))
                    foreach (var f in System.IO.Directory.GetFiles(cacheDir, "*", System.IO.SearchOption.AllDirectories))
                        try { System.IO.File.Delete(f); } catch { }   // in use by WebView2: cleared next time
            }
            Console.WriteLine("[cache] WebView2 cache cleared");
        } catch (Exception ex) { Console.WriteLine($"[cache] Couldn't clear the WebView2 cache: {ex.Message}"); }

        WebViewSecurity.Apply(_webView.CoreWebView2);
        _webView.CoreWebView2.WebMessageReceived += OnWebMessage;
        _webView.CoreWebView2.ProcessFailed      += OnWebViewProcessFailed;

        // Update check: requested by nav.js when a page loads (cached after the first check)

        // https://vexis.local/ is a made-up address that WebView2 maps to the install folder;
        // nothing goes over the network
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "vexis.local", AppDir, CoreWebView2HostResourceAccessKind.Allow);
        _webView.CoreWebView2.Navigate("https://vexis.local/home.html");

        // Wait until the page's script has defined receiveData (up to 10 s), then send it
        // the saved settings. Sending earlier would call a function that doesn't exist yet.
        Console.WriteLine("[startup] waiting for page...");
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            try
            {
                var r = await _webView.CoreWebView2.ExecuteScriptAsync("typeof receiveData");
                Console.WriteLine($"[startup] attempt {i+1}: {r}");
                if (r == "\"function\"")
                {
                    Console.WriteLine("[startup] ready");
                    await SendConfigViaScript();
                    _uiReady = true;
                    await RevealWindow();
                    return;
                }
            }
            catch (Exception ex) { Console.WriteLine($"[startup] {ex.Message}"); }
        }
        _uiReady = true;      // page never reported ready — keep pushing data, it may catch up
        await RevealWindow(); // and show the window anyway
    }

    // WebView2 runs the page in separate processes. If one dies (graphics driver reset,
    // out of memory, a crash at sign-in) the view goes blank and stops responding unless
    // it is reloaded or, when the browser process itself is gone, rebuilt.
    private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Console.WriteLine($"[webview] Process failed: {e.ProcessFailedKind} (reason {e.Reason}, exit code {e.ExitCode})");
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                BeginInvoke(async () => await RebuildWebViewAsync());
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
            case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                BeginInvoke(() => { try { _webView.CoreWebView2?.Reload(); } catch { /* rebuilt below if the control is gone */ } });
                break;
            // GPU and utility processes are restarted by WebView2 itself
        }
    }

    private async Task RebuildWebViewAsync()
    {
        // At most 3 rebuilds in 5 minutes, so a broken runtime can't loop forever
        if (DateTime.Now - _uiRebuildWindow > TimeSpan.FromMinutes(5)) { _uiRebuildWindow = DateTime.Now; _uiRebuilds = 0; }
        if (++_uiRebuilds > 3) { Console.WriteLine("[webview] Too many restarts — leaving the interface closed. Restart Vexis."); return; }
        Console.WriteLine("[webview] Rebuilding the interface");
        _uiReady = false;
        _uiTask  = null;
        var old = _webView;
        _webView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = System.Drawing.Color.Black };
        Controls.Add(_webView);
        Controls.Remove(old);
        try { old.Dispose(); } catch { }   // the old control is already broken; nothing to do
        await StartUiAsync();
    }

    // Swap the loading screen for the main window, and report driver problems
    private bool _revealed;
    private async Task RevealWindow()
    {
        if (_revealed) return;
        _revealed = true;
        await Task.Delay(400); // let the first reading render
        SplashForm.CloseSplash();
        if (!_startMinimized)
        {
            Opacity = 1; ShowInTaskbar = true;
            Activate();
        }
        int problems = StartupCheck.Problems;
        if (problems > 0)
            QueueNotification("Driver check",
                $"{problems} driver problem{(problems == 1 ? "" : "s")} found. Open Vexis → Security for details.",
                ToolTipIcon.Warning);
    }

    // Sends driver / Windows protection status to the Security page (main window + popouts)
    private void PushSecurityStatus()
    {
        string json = JsonSerializer.Serialize(DriverSetup.GetSecurityStatus(), _json);
        RunScriptEverywhere($"typeof onSecurityStatus==='function'&&onSecurityStatus({json})");
        string checks = JsonSerializer.Serialize(new
        {
            ranAt   = StartupCheck.RanAt == default ? null : StartupCheck.RanAt.ToString("HH:mm:ss"),
            results = StartupCheck.Results
        }, _json);
        RunScriptEverywhere($"typeof onStartupChecks==='function'&&onStartupChecks({checks})");
    }

    private void PushRecordings()
    {
        string json = JsonSerializer.Serialize(CsvRecorder.ListFiles(), _json);
        RunScriptEverywhere($"typeof onRecordings==='function'&&onRecordings({json})");
    }

    // Runs a script in the main window and every popout (safe from any thread)
    private void RunScriptEverywhere(string script)
    {
        BeginInvoke(() =>
        {
            _webView.CoreWebView2?.ExecuteScriptAsync(script);
            foreach (var p in _popouts.Values)
                if (!p.IsDisposed) _ = p.ExecuteScriptAsync(script);
        });
    }

    private static bool IsWebView2Installed()
    {
        try
        {
            return !string.IsNullOrEmpty(
                Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException) { return false; }
        catch { return true; } // unexpected error — let CreateAsync report it
    }

    private async Task SendConfigViaScript()
    {
        try
        {
            var payload = new
            {
                type = "config", colors = _config.Colors, settings = _config.Settings,
                colorProfiles = _config.ColorProfiles, fanCurves = _config.FanCurves,
                fanProfile = FanProfiles.Normalize(_config.FanProfile), fanPresets = FanProfiles.Describe(),
                lastPresetIdx = _config.LastPresetIdx, appVersion = AppVersionText, buildCommit = BuildCommit,
                startWithWindows = StartWithWindowsEnabled, zoom = Math.Round(_webView.ZoomFactor * 100)
            };
            string json = JsonSerializer.Serialize(payload, _json);
            await _webView.CoreWebView2.ExecuteScriptAsync(
                $"typeof onConfigReceived==='function'&&onConfigReceived({json})");
            Console.WriteLine("[startup] config injected");
        }
        catch (Exception ex) { Console.WriteLine($"[startup] config error: {ex.Message}"); }
    }

    private bool _isFullscreen = false;
    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        FormBorderStyle = _isFullscreen ? FormBorderStyle.None      : FormBorderStyle.Sizable;
        WindowState     = _isFullscreen ? FormWindowState.Maximized : FormWindowState.Normal;
    }

    // One pop-out per page: asking again brings the open one to the front
    private void OpenPopout(string page)
    {
        if (_popouts.TryGetValue(page, out var existing) && !existing.IsDisposed)
        { existing.Activate(); return; }
        // Messages from the pop-out arrive on its own UI thread. OpenRGB checks are answered
        // straight back to that window; everything else is handed to HandleMessage
        PopoutForm? form = null;
        form = new PopoutForm(page, _config, raw =>
        {
            Invoke(() =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var msg2 = System.Text.Json.JsonSerializer.Deserialize<IncomingMessage>(raw,
                            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (msg2 == null) return;

                        if (msg2.type == "openrgbProxy")
                        {
                            var (ok3, json3) = await OpenRgbProxyAsync(msg2);
                            string cid3 = msg2.callId ?? "0";
                            string esc3 = System.Text.Json.JsonSerializer.Serialize(json3);
                            await form!.ExecuteScriptAsync(
                                $"typeof orgbProxyResult==='function'&&orgbProxyResult('{cid3}',{(ok3 ? "true" : "false")},{esc3})");
                        }
                        else if (msg2.type == "checkOpenRGB")
                        {
                            bool connected = _orgbClient?.IsConnected ?? false;
                            string cid3 = msg2.callId ?? "0";
                            await form!.ExecuteScriptAsync(
                                $"typeof orgbCheckResult==='function'&&orgbCheckResult('{cid3}',{connected.ToString().ToLower()})");
                        }
                        // Everything else (settings, fan control, alerts, ...) goes through the
                        // main handler — previously these were silently dropped for popouts.
                        else if (msg2.type != null && !MainWindowOnly.Contains(msg2.type))
                            BeginInvoke(() => HandleMessage(raw));
                    }
                    catch (Exception ex) { Console.WriteLine($"[popout] Message from a pop-out failed: {ex.Message}"); }
                });
            });
        });
        _popouts[page] = form;
        form.FormClosed += (_, _) => _popouts.Remove(page);
        form.Show();
    }
}
