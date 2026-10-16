// V was here ✌  As of 2.13.0, 31% of the code in this file is still my original work (Vlottiz). The rest came later, some of it AI-assisted (see CONTRIBUTING.md). Don't take my word for it: git blame.
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;
using System.Text.Json.Serialization;

// ─── Main window ────────────────────────────────────────────────────────────────
// The hub of the app: owns the sensor service, fan controller, tray icon and the WebView2
// that shows the pages. Every 250 ms OnPollTick sends one JSON snapshot to every open page;
// pages send commands back through HandleMessage (one switch on msg.type).
// docs/ARCHITECTURE.md has the bigger picture.
public class MainForm : Form
{
    private WebView2                  _webView  = new() { DefaultBackgroundColor = System.Drawing.Color.Black };
    private readonly NotifyIcon       _tray     = new();
    private readonly ContextMenuStrip _trayMenu = new();

    private SensorService _sensor = null!;
    private FanController _fans   = null!;
    private AppConfig     _config = null!;

    // A WinForms timer ticks on the UI thread, which WebView2 calls must run on.
    // The sensor reads themselves happen on SensorService's own background timer.
    private System.Windows.Forms.Timer _pollTimer = new() { Interval = 250 };
    private int   _pollCount      = 0;
    private readonly CsvRecorder _recorder = new();
    private readonly ProcessMonitor _procs = new();
    private readonly DuplicateFinder _dupes = new();
    private SensorData? _lastData;   // last reading sent to the pages (includes fans)
    private float _lastLoggedTemp = 0;   // stops the high-temperature log line repeating every tick

    private readonly Dictionary<string, PopoutForm> _popouts = new();   // page id → open pop-out window

    // Keep the C# property names as the JSON keys, leave out nulls (pages treat a missing
    // key as "no sensor"), and allow NaN / Infinity from odd sensors instead of throwing
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy   = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling         = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;

    // Single source of truth: <Version> in Pcmonitor2_0.csproj
    public static readonly Version AppVersion =
        typeof(MainForm).Assembly.GetName().Version ?? new Version(0, 0, 0);
    public static string AppVersionText => $"{AppVersion.Major}.{AppVersion.Minor}.{AppVersion.Build}";
    // Git commit this build came from ("2.11.0+<sha>" in the informational version; the
    // release workflow sets it), shown on Info so a download can be matched to its source
    public static readonly string? BuildCommit = ReadBuildCommit();
    private static string? ReadBuildCommit()
    {
        string? iv = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<
            System.Reflection.AssemblyInformationalVersionAttribute>(typeof(MainForm).Assembly)?.InformationalVersion;
        int plus = iv?.IndexOf('+') ?? -1;
        string? sha = plus >= 0 ? iv![(plus + 1)..] : null;
        return sha != null && sha.Length >= 7 && sha.All(Uri.IsHexDigit) ? sha : null;
    }

    private readonly bool _startMinimized;

    // Cached "Start with Windows" state (schtasks query is slow — refreshed on change)
    public static bool StartWithWindowsEnabled { get; private set; }

    public MainForm(bool startMinimized = false)
    {
        _startMinimized = startMinimized;
        // WinForms only runs Load when the form is first shown — show it invisibly.
        // Minimized starts then hide to the tray; normal starts stay invisible behind
        // the loading screen until the page is ready (RevealWindow).
        Opacity       = 0;
        ShowInTaskbar = false;
        SuspendLayout();
        Text            = "Vexis";
        Size            = new System.Drawing.Size(420, 900);
        MinimumSize     = new System.Drawing.Size(380, 600);
        BackColor       = System.Drawing.Color.Black;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition   = FormStartPosition.Manual;
        _webView.Dock   = DockStyle.Fill;
        Controls.Add(_webView);
        ResumeLayout(false);
        Load        += OnLoad;
        FormClosing += OnClosing;
        Resize      += OnResize;
        ResizeEnd   += (_, _) => SavePlacement();
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        // The whole UI is WebView2. Windows 11 always has it; some Windows 10
        // machines don't (the installer adds it, but a copied folder won't).
        if (!IsWebView2Installed())
        {
            Console.WriteLine("[startup] WebView2 runtime not found.");
            SplashForm.CloseSplash();
            var answer = MessageBox.Show(
                "Vexis needs the Microsoft Edge WebView2 Runtime, which isn't installed on this PC.\n\n" +
                "Open the download page now? (Choose \"Evergreen Bootstrapper\", install it, then start Vexis again.)",
                "Vexis — WebView2 required", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://developer.microsoft.com/microsoft-edge/webview2/",
                    UseShellExecute = true
                });
            Application.Exit();
            return;
        }

        // Safety net: never leave the window invisible if a startup step hangs
        _ = Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ =>
        {
            try { BeginInvoke(async () => { if (!_revealed) { Console.WriteLine("[startup] slow start — showing window"); await RevealWindow(); } }); }
            catch { }   // window already closed
        });

        _config = AppConfig.Load();
        try { File.Delete(ExitRequestPath); } catch { }   // a leftover request must not close this launch
        RestorePlacement();
        if (_startMinimized)
            BeginInvoke(() =>
            {
                Hide();
                Opacity = 1; ShowInTaskbar = true;
                Console.WriteLine("[startup] Started minimized to the tray.");
            });
        _ = Task.Run(() => StartWithWindowsEnabled = StartupManager.IsEnabled());

        // Driver check on every launch (repairs PawnIO, reports broken/missing drivers)
        await Task.Run(() => StartupCheck.Run(SplashForm.SetStatus,
            r => SplashForm.AddCheck(r.level, $"{r.name}: {r.detail}")));

        SplashForm.SetStatus("Opening hardware sensors…");
        _sensor = await Task.Run(() => new SensorService(_config));
        SplashForm.SetStatus("Finding fans…");
        _fans   = new FanController();
        await Task.Run(() => _fans.Discover(_sensor.AllHardware));
        _fans.ApplyProfile(_config);   // saved curves / fan profile take effect at launch
        // Hand the fans back to the BIOS on every way out, not just a normal close: a crash
        // (unhandled error) or the process being shut down would otherwise leave them at
        // the last software speed until the PC restarts.
        Application.ApplicationExit                += (_, _) => RestoreFansSafely("exit");
        AppDomain.CurrentDomain.ProcessExit        += (_, _) => RestoreFansSafely("process exit");
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreFansSafely("crash");
        SetupTray();

        // Fans, alerts and recording run from the poll timer, independent of the interface,
        // so they keep working even if the page is slow, closed or being rebuilt.
        _sensor.Start();
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();

        if (_startMinimized)
        {
            // Started with Windows: right after sign-in the desktop, graphics driver and
            // WebView2 runtime may still be starting, and a WebView2 created inside a window
            // that has never been shown can come back blank and unresponsive. So only the
            // monitoring runs now; the interface is built the first time the window is opened.
            Console.WriteLine("[startup] Running in the tray — the interface loads when Vexis is opened.");
            await RevealWindow();   // driver-problem notification only; the window stays hidden
            // Pages normally ask for the update check; nothing is open yet, so ask once here
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => { try { BeginInvoke(() => PushUpdateInfo(force: false)); } catch { /* window already closed */ } });
            return;
        }

        SplashForm.SetStatus("Loading interface…");
        await StartUiAsync();
    }

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

    // Runs a script in the main window and every popout (safe from any thread)
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

    private void PushRecordings()
    {
        string json = JsonSerializer.Serialize(CsvRecorder.ListFiles(), _json);
        RunScriptEverywhere($"typeof onRecordings==='function'&&onRecordings({json})");
    }

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

    private async void OnPollTick(object? sender, EventArgs e)
    {
        try
        {
            if (++_exitCheck % 4 == 0 && File.Exists(ExitRequestPath)) { ExitForInstaller(); return; }
            var data = _sensor.GetLatestData();
            _fans.Update(data, _config);
            CheckTempAlerts(data);

            data.fans = _fans.GetSnapshot();
            data.fan_profile = FanProfiles.Normalize(_config.FanProfile);
            data.crash = CrashHelper.Pending;
            _lastData = data;
            _recorder.Append(data);
            data.recording = _recorder.Status;
            lock (_inbox) data.alerts = _inbox.Count > 0 ? _inbox.ToList() : null;

            if (!_uiReady && _popouts.Count == 0) return;   // nothing open to show it in
            string json = JsonSerializer.Serialize(data, _json);

            if (_uiReady && _webView.CoreWebView2 != null)
                await _webView.CoreWebView2.ExecuteScriptAsync(
                    $"typeof receiveData==='function'&&receiveData({json})");

            foreach (var kv in _popouts.ToList())
            {
                if (!kv.Value.IsDisposed) await kv.Value.PushData(json);
                else _popouts.Remove(kv.Key);
            }

            // Once a minute (240 × 250 ms) write a one-line summary to debug.log, so a log
            // from a user shows what the hardware was doing
            _pollCount++;
            if (_pollCount % 240 == 0)
            {
                var d = data;
                string cpuStr  = d.cpu_temp.HasValue    ? $"{d.cpu_temp:F1}°C"       : "n/a";
                string gpuStr  = d.gpu_temp.HasValue    ? $"{d.gpu_temp:F1}°C"       : "n/a";
                string pkgStr  = d.pkg_power.HasValue   ? $"{d.pkg_power:F1}W"       : "n/a";
                string clkStr  = d.avg_clk.HasValue     ? $"{d.avg_clk/1000f:F2}GHz" : "n/a";
                string ramStr  = d.ram_used_gb.HasValue ? $"{d.ram_used_gb:F1}GB used": "n/a";
                string gpuLoad = d.gpu_load.HasValue    ? $"{d.gpu_load:F0}%"        : "n/a";
                Console.WriteLine($"[health] CPU {cpuStr} @ {clkStr} ({pkgStr}) | GPU {gpuStr} load={gpuLoad} | RAM {ramStr}");

                var fans = data.fans;
                if (fans?.Count > 0)
                {
                    var fanSummary = string.Join(", ", fans.Select(f =>
                        $"{f.name.Replace("Fan","").Replace(" ","")}: {f.rpm:F0}RPM/{f.pct:F0}%"));
                    Console.WriteLine($"[health] Fans — {fanSummary}");
                }
            }

            // Log-only warnings (the user-facing alerts are CheckTempAlerts). Logs again only
            // after a 2 °C change, and says "normal" once it's back under 85 °C
            float cpuTemp = data.cpu_temp ?? 0;
            if (cpuTemp > 90 && Math.Abs(cpuTemp - _lastLoggedTemp) > 2)
            {
                Console.WriteLine($"[warn] CPU temperature HIGH: {cpuTemp:F1}°C — check cooling!");
                _lastLoggedTemp = cpuTemp;
            }
            else if (cpuTemp < 85 && _lastLoggedTemp > 90)
            {
                Console.WriteLine($"[health] CPU temperature normal: {cpuTemp:F1}°C");
                _lastLoggedTemp = cpuTemp;
            }
            float gpuTemp = data.gpu_temp ?? 0;
            if (gpuTemp > 95)
                Console.WriteLine($"[warn] GPU temperature CRITICAL: {gpuTemp:F1}°C");
        }
        catch (Exception ex) { Console.WriteLine($"[poll] {ex.Message}"); }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only Vexis's own pages may send commands (navigation is locked to them anyway)
        if (!SafeLinks.IsAppPage(e.Source)) { Console.WriteLine($"[msg] ignored message from {e.Source}"); return; }
        try { HandleMessage(e.TryGetWebMessageAsString()); }
        catch (Exception ex) { Console.WriteLine($"[msg] error: {ex.Message}"); }
    }

    // Messages that only make sense for the main window (popouts must not drive it)
    private static readonly HashSet<string> MainWindowOnly =
        new() { "navigate", "popOut", "minimize", "exit", "toggleFullscreen", "requestConfig", "getLogs", "zoom" };

    private void HandleMessage(string raw)
    {
        try
        {
            Console.WriteLine($"[msg] {raw[..Math.Min(80,raw.Length)]}");   // first 80 chars, enough to see the type
            var msg = JsonSerializer.Deserialize<IncomingMessage>(raw, _json);
            if (msg == null) return;

            switch (msg.type)
            {
                case "getLogs":
                    int afterSeq = msg.seq ?? 0;
                    var (logLines, newSeq) = AppLog.GetSince(afterSeq);
                    if (logLines.Length > 0)
                    {
                        var logJson = System.Text.Json.JsonSerializer.Serialize(
                            new { lines = logLines, seq = newSeq });
                        Invoke(() => { _ = _webView.CoreWebView2?.ExecuteScriptAsync(
                            $"typeof updateLogs==='function'&&updateLogs({logJson})"); });
                    }
                    break;

                case "setStartup":
                {
                    bool want = msg.enabled == true;
                    _ = Task.Run(() =>
                    {
                        StartWithWindowsEnabled = StartupManager.SetEnabled(want);
                        RunScriptEverywhere(
                            $"typeof navSetStartupState==='function'&&navSetStartupState({(StartWithWindowsEnabled ? "true" : "false")})");
                    });
                    break;
                }

                case "zoom":
                    Invoke(() => ZoomHelper.Change(_webView, msg.delta ?? 0)); // ZoomFactorChanged saves + updates the label
                    break;

                case "recordStart":
                    _recorder.Start(_lastData ?? _sensor.GetLatestData(), msg.record);
                    PushRecordings();
                    break;

                case "factoryReset":
                    Invoke(FactoryReset);
                    break;

                case "recordStop":
                    _recorder.Stop();
                    PushRecordings();
                    break;

                // ── Processes page (Task-Manager-style list) ──────────────────────
                case "getProcesses":                 // sampled off the UI thread (~100 ms)
                {
                    bool reset = msg.enabled == true;       // page just opened: resend icons
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            if (reset) _procs.ResetIcons();
                            var snap = _procs.Sample();
                            RunScriptEverywhere($"typeof onProcesses==='function'&&onProcesses({JsonSerializer.Serialize(snap, _json)})");
                        }
                        catch (Exception ex) { Console.WriteLine($"[procs] {ex.Message}"); }
                    });
                    break;
                }

                case "endTask":
                {
                    int? pid = msg.pid; string? exe = msg.file;
                    _ = Task.Run(() =>
                    {
                        var (ok, text) = _procs.End(pid, exe);
                        RunScriptEverywhere($"typeof onEndTask==='function'&&onEndTask({(ok ? "true" : "false")},{JsonSerializer.Serialize(text)})");
                    });
                    break;
                }

                case "listRecordings":
                    PushRecordings();
                    break;

                case "readRecording":
                {
                    var path = CsvRecorder.ResolveFile(msg.file);
                    if (path == null) break;
                    // Shared read: the file may still be being recorded
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    const int Max = 25 * 1024 * 1024;
                    var buf = new byte[Math.Min(fs.Length, Max)];
                    int read = 0, n;
                    while (read < buf.Length && (n = fs.Read(buf, read, buf.Length - read)) > 0) read += n;
                    string text = System.Text.Encoding.UTF8.GetString(buf, 0, read).TrimStart('\uFEFF');
                    string json = JsonSerializer.Serialize(new { name = Path.GetFileName(path), text, truncated = fs.Length > Max }, _json);
                    RunScriptEverywhere($"typeof onRecordingData==='function'&&onRecordingData({json})");
                    break;
                }

                case "showRecording":
                {
                    var path = CsvRecorder.ResolveFile(msg.file);
                    if (path != null) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
                    break;
                }

                case "deleteRecording":
                {
                    var path = CsvRecorder.ResolveFile(msg.file);
                    if (path != null && !(_recorder.IsRecording && _recorder.Status.file == msg.file))
                    {
                        try { File.Delete(path); Console.WriteLine($"[record] Deleted {msg.file}"); }
                        catch (Exception ex) { Console.WriteLine($"[record] Delete failed: {ex.Message}"); }
                    }
                    PushRecordings();
                    break;
                }

                case "openLogFolder":
                    Directory.CreateDirectory(CsvRecorder.Folder);
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{CsvRecorder.Folder}\"");
                    break;

                case "clearAlerts":
                    Invoke(ClearInbox);
                    break;

                case "testAlert":
                    // 5 s delay so there is time to minimize Vexis and see the tray icon blink
                    _ = Task.Delay(5000).ContinueWith(_ => BeginInvoke(() => QueueNotification("test alert",
                        "Temperature alerts are working. You'll see this when a limit is passed.",
                        ToolTipIcon.Info)));
                    break;

                case "getSecurityStatus":
                    PushSecurityStatus();
                    break;

                case "runStartupCheck":
                    _ = Task.Run(() => { StartupCheck.Run(); PushSecurityStatus(); });
                    break;

                case "installPawnIO":
                    _ = Task.Run(() => { DriverSetup.EnsurePawnIo(forceRepair: true); PushSecurityStatus(); });
                    break;

                case "restoreSecurity":
                    _ = Task.Run(() => { DriverSetup.RestoreWindowsSecurity(); PushSecurityStatus(); });
                    break;

                // ── Settings from the pages, merged into config.json ──
                case "saveSettings":
                    if (msg.settings != null)
                    {
                        _config.Settings ??= new Dictionary<string,string>();
                        foreach (var kv in msg.settings)
                            _config.Settings[kv.Key] = kv.Value;
                        _config.Save();
                        Console.WriteLine("[config] Settings saved");
                        if (msg.settings.Keys.Any(k => k.StartsWith("alert")))
                        {
                            // New limits/state: evaluate from scratch (no latch, no cooldown)
                            _alertActive.Clear(); _alertLast.Clear();
                            var st = _config.Settings;
                            Console.WriteLine($"[alert] settings: enabled={st.GetValueOrDefault("alertsEnabled", "false")} " +
                                              $"cpu={st.GetValueOrDefault("alertCpu", "90")} gpu={st.GetValueOrDefault("alertGpu", "85")}");
                        }
                    }
                    break;

                case "saveColors":
                    if (msg.colors != null)
                        foreach (var kv in msg.colors)
                            _config.Colors[kv.Key] = kv.Value;
                    _config.Save();
                    break;

                case "saveProfile":
                    if (msg.slot.HasValue)
                    { _config.ColorProfiles[msg.slot.Value] = msg.colors ?? new(); _config.Save(); }
                    break;

                case "deleteProfile":
                    if (msg.slot.HasValue)
                    { _config.ColorProfiles.Remove(msg.slot.Value); _config.Save(); }
                    break;

                case "requestConfig":
                    _ = SendConfigViaScript();
                    // Also push current sensor data immediately so the page
                    // doesn't have to wait up to 250ms for the next poll tick.
                    // This is what makes fans.html show data right away on navigate.
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(100); // brief delay for config to settle
                        try
                        {
                            var snap = _sensor.GetLatestData();
                            snap.fans = _fans.GetSnapshot();
                            string snapJson = JsonSerializer.Serialize(snap, _json);
                            Invoke(() => { _ = _webView.CoreWebView2?.ExecuteScriptAsync(
                                $"typeof receiveData==='function'&&receiveData({snapJson})"); });
                        }
                        catch (Exception ex) { Console.WriteLine($"[popout] First data push failed: {ex.Message}"); }
                    });
                    break;

                // Page change from the side menu. Navigation is locked to vexis.local
                // (WebViewSecurity), so a path here can only open one of the app's own files
                case "navigate":
                    if (msg.file != null)
                        _webView.CoreWebView2.Navigate($"https://vexis.local/{msg.file}");
                    break;

                case "popOut":
                    if (msg.page != null) Invoke(() => OpenPopout(msg.page));
                    break;

                case "checkUpdate":                 // enabled:true = "check again" button
                    PushUpdateInfo(force: msg.enabled == true);
                    break;

                case "installUpdate":
                    _ = Task.Run(InstallUpdateAsync);
                    break;

                case "openUrl":
                    // Shell-execute opens whatever it's given — a web page, but also a program
                    // or a file. Only hand it https:// links so a page can never start anything else.
                    if (SafeLinks.IsWebLink(msg.url, out var link))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        { FileName = link, UseShellExecute = true });
                    else
                        Console.WriteLine($"[msg] openUrl refused (not an https link): {msg.url}");
                    break;

                // ── RGB ──
                // The RGB page can't open a TCP socket itself, so it sends OpenRGB requests here;
                // the reply comes back through orgbProxyResult(callId, ...) matched by callId
                case "openrgbProxy":
                    Console.WriteLine($"[rgb] proxy: {msg.method} {msg.path}");
                    _ = Task.Run(async () =>
                    {
                        string cid2 = msg.callId ?? "0";
                        var (ok, json) = await OpenRgbProxyAsync(msg);
                        string payload = System.Text.Json.JsonSerializer.Serialize(json);
                        Invoke(() => { _ = _webView.CoreWebView2?.ExecuteScriptAsync(
                            $"typeof orgbProxyResult==='function'&&orgbProxyResult('{cid2}',{(ok ? "true" : "false")},{payload})"); });
                    });
                    break;

                case "killOpenRGB":
                    OpenRgbLauncher.StopAll();
                    _orgbClient?.ResetFailedDevices();
                    _orgbClient = null;
                    break;

                case "checkOpenRGB":
                {
                    string cid3 = msg.callId ?? "0";
                    string runEsc = JsonSerializer.Serialize(OpenRgbLauncher.IsRunning() ? "{\"running\":true}" : "{\"running\":false}");
                    Invoke(() => { _ = _webView.CoreWebView2?.ExecuteScriptAsync(
                        $"typeof orgbProxyResult==='function'&&orgbProxyResult('{cid3}',true,{runEsc})"); });
                    break;
                }

                case "launchOpenRGB":
                    if (OpenRgbLauncher.IsRunning()) { Console.WriteLine("[rgb] OpenRGB already running"); break; }
                    _sensor.PausePoll(5000);   // OpenRGB scans the RAM bus at start; keep LHM off it meanwhile
                    OpenRgbLauncher.Start();
                    break;

                case "launchOpenRGBGui":
                    // Restart it so its window opens; wait off the UI thread while the old copy exits
                    _orgbClient = null;
                    _ = Task.Run(async () =>
                    {
                        OpenRgbLauncher.StopAll();
                        await Task.Delay(800);
                        OpenRgbLauncher.Start();
                    });
                    break;

                // Changing one fan by hand leaves a preset profile (back to Custom)
                case "fanSetManual":
                    if (msg.fanName != null) { _fans.LeavePreset(_config); _fans.SetManual(msg.fanName, msg.value ?? 50f); UpdateTrayProfile(); }
                    break;
                case "setFanPump":
                    if (msg.fanName != null) _fans.SetPump(msg.fanName, msg.enabled == true, _config);
                    break;
                case "fanSetAuto":
                    if (msg.fanName != null) { _fans.LeavePreset(_config); _fans.SetAuto(msg.fanName); UpdateTrayProfile(); }
                    break;
                case "fanSetCurveMode":
                    if (msg.fanName != null) { _fans.LeavePreset(_config); _fans.SetCurveMode(msg.fanName, msg.enabled ?? false); UpdateTrayProfile(); }
                    break;
                case "saveFanCurve":
                    if (msg.fanCurve != null)
                    {
                        _fans.LeavePreset(_config);
                        _config.UpsertCurve(msg.fanCurve); _fans.SetCurveMode(msg.fanCurve.FanName, msg.fanCurve.Enabled);
                        UpdateTrayProfile();
                    }
                    break;
                // ── Storage page: duplicate file finder ─────────────────────────
                case "dupPickFolder":
                {
                    using var dlg = new FolderBrowserDialog { Description = "Choose a folder to search for duplicate files", UseDescriptionForTitle = true };
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                        RunScriptEverywhere($"typeof onDupFolder==='function'&&onDupFolder({JsonSerializer.Serialize(dlg.SelectedPath)})");
                    break;
                }
                case "dupStart":
                {
                    // Only full paths to folders that exist; anything else from the page is dropped
                    var folders = (msg.dup?.paths ?? new()).Select(DuplicateFinder.CheckFolder).OfType<string>().ToList();
                    if (folders.Count > 0 && !_dupes.Running)
                        _dupes.Start(new DuplicateFinder.Options(folders, (long)(Math.Clamp(msg.dup!.minMb, 0, 1_000_000) * 1048576), msg.dup.includeSystem),
                            pr => RunScriptEverywhere($"typeof onDupProgress==='function'&&onDupProgress({JsonSerializer.Serialize(pr, _json)})"),
                            res => RunScriptEverywhere($"typeof onDupResults==='function'&&onDupResults({JsonSerializer.Serialize(res, _json)})"));
                    break;
                }
                case "dupCancel":
                    _dupes.Cancel();
                    break;
                case "dupRecycle":
                    if (msg.dup?.files is { Count: > 0 } files)
                        _ = Task.Run(() =>
                        {
                            var (removed, freed, skipped) = _dupes.Recycle(files);
                            RunScriptEverywhere($"typeof onDupRecycled==='function'&&onDupRecycled({JsonSerializer.Serialize(new { removed, freed, skipped }, _json)})");
                        });
                    break;
                case "dupReveal":
                    // Only files the last scan found, never an arbitrary path from the page
                    if (_dupes.IsInResults(msg.file) && File.Exists(msg.file))
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{msg.file}\"");
                    break;

                case "crashOpenLog":  CrashHelper.OpenLog(); break;
                case "crashReport":   CrashHelper.OpenReport(_lastData?.info); break;
                case "crashDismiss":  CrashHelper.Dismiss(); break;

                case "setFanProfile":
                    SetFanProfile(msg.profile);
                    break;

                // Takes ~8 s, so it runs on a worker thread; the result goes back as a web message
                case "fanHealthCheck":
                    if (msg.fanName != null)
                    {
                        string name = msg.fanName;
                        Task.Run(() =>
                        {
                            var result = _fans.RunHealthCheck(name);
                            string rjson = JsonSerializer.Serialize(new
                            {
                                type="healthResult", fanName=result.FanName,
                                status=result.Status, message=result.Message, readings=result.Readings
                            }, _json);
                            Invoke(() => _webView.CoreWebView2?.PostWebMessageAsString(rjson));
                        });
                    }
                    break;

                case "minimize": Invoke(() => { WindowState = FormWindowState.Minimized; }); break;
                case "close":    Invoke(() => Hide()); break;   // to the tray; Vexis keeps running
                case "toggleFullscreen": Invoke(() => ToggleFullscreen()); break;
                case "exit":
                    Invoke(() => { _tray.Visible = false; Application.Exit(); });
                    break;
                default: Console.WriteLine($"[msg] unknown: {msg.type}"); break;
            }
        }
        catch (Exception ex) { Console.WriteLine($"[msg] error: {ex.Message}"); }
    }

    private bool _isFullscreen = false;
    private OpenRGBClient? _orgbClient;
    private readonly SemaphoreSlim _orgbLock = new(1, 1);
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

    // ── OpenRGB proxy (shared by the main window and popouts) ──────────────────
    // Paths (REST-like, from rgb.html):
    //   GET  /devices               → device list JSON
    //   PUT  /devices/{i}/leds      → [{r,g,b}, ...]
    //   PUT  /devices/{i}/mode      → {custom:true}            switch to Direct/Custom/Static
    //                                 {mode:n}                  switch to mode n
    //                                 {mode:n, r, g, b}         mode n with that colour
    //                                                           (mode-specific colours, e.g. GPU Static)
    //                                 {mode:n, brightness:0-100} hardware brightness, if the mode has one
    private async Task<(bool ok, string json)> OpenRgbProxyAsync(IncomingMessage msg)
    {
        await _orgbLock.WaitAsync();
        try
        {
            if (_orgbClient == null || !_orgbClient.IsConnected)
            {
                _orgbClient?.Dispose();
                _orgbClient = new OpenRGBClient(msg.port ?? 6742);
                await _orgbClient.ConnectAsync();
            }

            string path = msg.path ?? "/devices";
            if (path == "/devices")
                return (true, await _orgbClient.GetDevicesJsonAsync());

            var parts = path.Split('/');
            if (parts.Length < 4 || !uint.TryParse(parts[2], out uint dev) || msg.body == null)
                return (true, "{}");

            if (parts[3] == "leds")
            {
                var leds = JsonSerializer.Deserialize<List<Dictionary<string, int>>>(msg.body)!;
                await _orgbClient.SetLedsAsync(dev, leds);
            }
            else if (parts[3] == "mode" && msg.method == "PUT")
            {
                var m = JsonDocument.Parse(msg.body).RootElement;
                if (m.TryGetProperty("custom", out var cv) && cv.ValueKind == JsonValueKind.True)
                    await _orgbClient.SetCustomModeAsync(dev);
                else if (m.TryGetProperty("mode", out var mv))
                {
                    (byte, byte, byte)? color = m.TryGetProperty("r", out var rv)
                        ? ((byte)rv.GetInt32(),
                           (byte)(m.TryGetProperty("g", out var gv) ? gv.GetInt32() : 0),
                           (byte)(m.TryGetProperty("b", out var bv) ? bv.GetInt32() : 0))
                        : null;
                    int? bright = m.TryGetProperty("brightness", out var brv) ? brv.GetInt32() : null;
                    await _orgbClient.SetModeAsync(dev, mv.GetInt32(), color, bright);
                }
            }
            return (true, "{}");
        }
        catch (Exception ex)
        {
            try { _orgbClient?.Dispose(); } catch { }   // already closed; don't leak the old socket
            _orgbClient = null;
            Console.WriteLine($"[rgb] proxy error: {ex.Message}");
            return (false, ex.Message);
        }
        finally { _orgbLock.Release(); }
    }

    // Tray icon and its right-click menu (Show, fan profiles, Exit). Double-click opens the window
    private void SetupTray()
    {
        _trayMenu.Items.Clear();
        _trayMenu.Items.Add("Show", null, (_, _) => ShowDashboard());
        _trayMenu.Items.Add(new ToolStripSeparator());
        // Fan profile, switchable without opening the window
        _trayProfileMenu = new ToolStripMenuItem("Fan profile");
        foreach (var p in FanProfiles.All)
        {
            string name = p;
            _trayProfileMenu.DropDownItems.Add(new ToolStripMenuItem(FanProfiles.Title(name), null, (_, _) => SetFanProfile(name)) { Tag = name });
        }
        _trayMenu.Items.Add(_trayProfileMenu);
        UpdateTrayProfile();
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Exit", null, (_, _) => { _tray.Visible = false; Application.Exit(); });
        Icon? appIcon = null;
        try {
            string icoPath = Path.Combine(AppDir, "vexis.ico");
            if (File.Exists(icoPath)) appIcon = new Icon(icoPath);
        } catch (Exception ex) { Console.WriteLine($"[tray] Couldn't load vexis.ico, using the default icon: {ex.Message}"); }
        _tray.Text = "Vexis Hardware Monitoring";
        _trayIcon      = appIcon ?? SystemIcons.Application;
        _trayAlertIcon = MakeAlertIcon(_trayIcon);
        _tray.Icon = _trayIcon;
        if (appIcon != null) this.Icon = appIcon;
        _tray.ContextMenuStrip = _trayMenu; _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowDashboard();
        _tray.BalloonTipClicked += (_, _) => ShowDashboard();
        _blinkTimer.Tick += (_, _) =>
        {
            _blinkOn = !_blinkOn;
            _tray.Icon = _blinkOn ? _trayAlertIcon : _trayIcon;
        };
        // Seeing the window counts as "seen": stop blinking (the alert list stays until cleared)
        Activated += (_, _) => { if (Visible && Opacity > 0) StopBlink(); };
    }

    private ToolStripMenuItem? _trayProfileMenu;

    private void SetFanProfile(string? profile)
    {
        _config.FanProfile = FanProfiles.Normalize(profile);
        _config.Save();
        _fans.ApplyProfile(_config);
        UpdateTrayProfile();
        RunScriptEverywhere($"typeof onFanProfile==='function'&&onFanProfile('{_config.FanProfile}')");
    }

    private void UpdateTrayProfile()
    {
        if (_trayProfileMenu == null) return;
        string cur = FanProfiles.Normalize(_config?.FanProfile);
        _trayProfileMenu.Text = "Fan profile: " + FanProfiles.Title(cur);
        foreach (ToolStripMenuItem i in _trayProfileMenu.DropDownItems) i.Checked = (string?)i.Tag == cur;
    }

    private void ShowDashboard()
    {
        Show(); WindowState = FormWindowState.Normal; Activate(); StopBlink();
        if (_webView.CoreWebView2 == null) _ = StartUiAsync();   // started in the tray: build the interface now
    }

    // ── Alert inbox: what triggered a notification, shown in the app until cleared ──
    private readonly List<AlertEntry> _inbox = new();
    private readonly System.Windows.Forms.Timer _blinkTimer = new() { Interval = 600 };
    private Icon _trayIcon = SystemIcons.Application, _trayAlertIcon = SystemIcons.Warning;
    private bool _blinkOn;

    private void AddToInbox(string title, string text)
    {
        lock (_inbox)
        {
            _inbox.Add(new AlertEntry(Guid.NewGuid().ToString("N")[..8], DateTime.Now.ToString("HH:mm:ss"), title, text));
            if (_inbox.Count > 30) _inbox.RemoveAt(0);
        }
        // Blink the tray icon until the window is looked at
        if (!(Visible && Opacity > 0 && ContainsFocus))
        {
            _blinkTimer.Start();
            _tray.Text = $"Vexis — {_inbox.Count} alert{(_inbox.Count == 1 ? "" : "s")}";
        }
    }

    private void StopBlink()
    {
        if (!_blinkTimer.Enabled) return;
        _blinkTimer.Stop(); _blinkOn = false;
        _tray.Icon = _trayIcon;
        _tray.Text = "Vexis Hardware Monitoring";
    }

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

    private void ClearInbox()
    {
        lock (_inbox) _inbox.Clear();
        StopBlink();
        Console.WriteLine("[alert] Alert list cleared");
    }

    // App icon with a red dot in the corner, alternated with the normal icon while blinking
    private static Icon MakeAlertIcon(Icon baseIcon)
    {
        try
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawIcon(new Icon(baseIcon, 32, 32), new Rectangle(0, 0, 32, 32));
                g.FillEllipse(Brushes.White, 15, 15, 17, 17);
                using var red = new SolidBrush(Color.FromArgb(235, 40, 40));
                g.FillEllipse(red, 17, 17, 13, 13);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
        catch { return SystemIcons.Warning; }
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

    // ── Temperature alerts (tray notification) ─────────────────────────────────
    // Settings (shared config): alertsEnabled, alertCpu, alertGpu (°C).
    // Fires once when a limit is crossed, re-arms after dropping 5 °C below it,
    // and repeats at most every 5 minutes per sensor.
    private readonly HashSet<string>             _alertActive = new();
    private readonly Dictionary<string, DateTime> _alertLast  = new();

    // ── Tray notification queue ─────────────────────────────────────────────────
    // A NotifyIcon shows one balloon at a time and a new ShowBalloonTip replaces the
    // one on screen — a GPU alert 1.5 s after a CPU alert wiped the CPU one out.
    // Alerts raised close together are merged into one notification, and the next
    // one waits until the previous has had time to be seen. Windows also tends to
    // drop balloons from a tray icon that was only just created, hence the startup
    // delay before alerts are evaluated.
    private static readonly TimeSpan AlertStartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BalloonGap        = TimeSpan.FromSeconds(7);
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly List<(string Title, string Text, ToolTipIcon Icon)> _pendingBalloons = new();
    private DateTime _lastBalloon = DateTime.MinValue;

    private void QueueNotification(string title, string text, ToolTipIcon icon)
    {
        if (icon == ToolTipIcon.Warning || title == "test alert") AddToInbox(title, text);
        _pendingBalloons.Add((title, text, icon));
        FlushNotifications();
    }

    // Called on every poll tick (UI thread) and when something is queued
    private void FlushNotifications()
    {
        if (_pendingBalloons.Count == 0 || DateTime.Now - _lastBalloon < BalloonGap) return;
        var items = _pendingBalloons.ToList();
        _pendingBalloons.Clear();
        string title = items.Count == 1 ? $"Vexis — {items[0].Title}" : "Vexis — temperatures high";
        string text  = string.Join("\n", items.Select(i => i.Text));
        var icon     = items.Any(i => i.Icon == ToolTipIcon.Warning) ? ToolTipIcon.Warning : items[0].Icon;
        _tray.ShowBalloonTip(8000, title, text, icon);
        _lastBalloon = DateTime.Now;
        Console.WriteLine($"[alert] notification shown: {title} | {text.Replace("\n", " | ")}");
    }

    private void CheckTempAlerts(SensorData d)
    {
        FlushNotifications();
        var s = _config.Settings;
        if (s == null || !s.TryGetValue("alertsEnabled", out var on) || on != "true")
        {
            d.alert_status = "Alerts are off";
            return;
        }
        float Limit(string key, float def) =>
            s.TryGetValue(key, out var v) && float.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var f) && f > 0 ? f : def;
        var parts = new List<string>
        {
            CheckAlert("CPU", d.cpu_temp, Limit("alertCpu", 90)),
            CheckAlert("GPU", d.gpu_temp, Limit("alertGpu", 85))
        };
        // Hot spot and VRAM usually overheat before the core; only on GPUs that report them
        if (d.gpu?.temp_hotspot != null) parts.Add(CheckAlert("GPU hot spot", d.gpu.temp_hotspot, Limit("alertGpuHotspot", 100)));
        if (d.gpu?.temp_mem     != null) parts.Add(CheckAlert("GPU memory",   d.gpu.temp_mem,     Limit("alertGpuMem", 100)));
        d.alert_status = string.Join(" · ", parts);
    }

    // Returns a short status for the settings panel, e.g. "CPU 55°/40° sent 14:02"
    private string CheckAlert(string name, float? temp, float limit)
    {
        if (temp is not float t) return $"{name} no reading";
        string state = $"{name} {t:F0}°/{limit:F0}°";
        if (t >= limit)
        {
            if (_alertActive.Contains(name))
                return $"{state} sent {_alertLast[name]:HH:mm}";
            if (_alertLast.TryGetValue(name, out var last) && DateTime.Now - last < TimeSpan.FromMinutes(5))
                return $"{state} waiting (5 min limit)";
            if (DateTime.Now - _startedAt < AlertStartupDelay)
                return $"{state} starting up";
            _alertActive.Add(name);
            _alertLast[name] = DateTime.Now;
            QueueNotification($"{name} temperature high",
                $"{name} is at {t:F0} °C (your limit is {limit:F0} °C).", ToolTipIcon.Warning);
            Console.WriteLine($"[alert] {name} {t:F1}°C ≥ limit {limit:F0}°C — notification queued");
            return $"{state} sent {DateTime.Now:HH:mm}";
        }
        if (t < limit - 5) _alertActive.Remove(name);
        return _alertActive.Contains(name) ? $"{state} cooling" : $"{state} ok";
    }
    // Minimising hides the window to the tray instead of the taskbar
    private void OnResize(object? s, EventArgs e) { if (WindowState==FormWindowState.Minimized) Hide(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _fans?.RestoreAll(); } catch (Exception ex) { Console.WriteLine($"[fans] Couldn't hand the fans back on close: {ex.Message}"); }
            _recorder.Dispose();
            _blinkTimer.Dispose();
            _pollTimer.Dispose(); _sensor?.Dispose(); _tray.Dispose(); _webView.Dispose();
            foreach (var p in _popouts.Values) if (!p.IsDisposed) p.Dispose();
        }
        base.Dispose(disposing);
    }
}

// The shape of every sendToHost({...}) message from the pages. One class for all of them:
// each message type only fills the fields it needs and the rest stay null.
public class IncomingMessage
{
    public string?                     type     { get; set; }
    public string?                     file     { get; set; }
    public string?                     page     { get; set; }
    public string?                     url      { get; set; }
    public Dictionary<string, string>? colors   { get; set; }
    public Dictionary<string, string>? settings { get; set; }
    public string? method  { get; set; }
    public string? path    { get; set; }
    public string? body    { get; set; }
    public int?    port    { get; set; }
    public string? callId  { get; set; }
    public int?                        slot     { get; set; }
    public string?                     fanName  { get; set; }
    public float?                      value    { get; set; }
    public bool?                       enabled  { get; set; }
    public FanCurve?                   fanCurve { get; set; }
    public int?  seq      { get; set; }
    public int?  delta    { get; set; } // zoom: +1 / -1 / 0 (reset)
    public RecordOptions? record { get; set; } // recordStart: what to record
    public int?  pid      { get; set; } // endTask: one process (otherwise every process named "file")
    public string? profile { get; set; } // setFanProfile: custom | silent | balanced | performance
    public DupRequest? dup { get; set; } // duplicate finder: scan options / files to recycle
}

public class DupRequest
{
    public List<string>? paths { get; set; }          // folders / drives to scan
    public double        minMb { get; set; } = 1;     // ignore smaller files
    public bool          includeSystem { get; set; }  // also scan Windows / Program Files / AppData
    public List<string>? files { get; set; }          // dupRecycle: files to send to the Recycle Bin
}
