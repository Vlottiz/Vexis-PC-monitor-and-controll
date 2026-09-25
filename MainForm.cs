using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;
using System.Text.Json.Serialization;

public class MainForm : Form
{
    private readonly WebView2         _webView  = new();
    private readonly NotifyIcon       _tray     = new();
    private readonly ContextMenuStrip _trayMenu = new();

    private SensorService _sensor = null!;
    private FanController _fans   = null!;
    private AppConfig     _config = null!;

    private System.Windows.Forms.Timer _pollTimer = new() { Interval = 250 };
    private int   _pollCount      = 0;
    private readonly CsvRecorder _recorder = new();
    private SensorData? _lastData;   // last reading sent to the pages (includes fans)
    private float _lastLoggedTemp = 0;

    private readonly Dictionary<string, PopoutForm> _popouts = new();

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
            catch { }
        });

        _config = AppConfig.Load();
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
        Application.ApplicationExit += (_, _) => { try { _fans.RestoreAll(); } catch { } };
        SetupTray();
        SplashForm.SetStatus("Loading interface…");

        var webViewOpts = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions(
            additionalBrowserArguments: "--disable-web-security --allow-running-insecure-content --allow-file-access-from-files");

        var webViewEnv = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Vexis", "WebView2Cache"),
            options: webViewOpts);
        await _webView.EnsureCoreWebView2Async(webViewEnv);

        // Page zoom (− / + buttons, Ctrl+scroll): restore last level, remember changes
        _webView.ZoomFactor = ZoomHelper.Parse(_config.Settings);
        _webView.ZoomFactorChanged += (_, _) =>
        {
            _config.Settings ??= new Dictionary<string, string>();
            _config.Settings["zoom"] = _webView.ZoomFactor.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            _config.Save();
            _webView.CoreWebView2?.ExecuteScriptAsync(ZoomHelper.Script(_webView.ZoomFactor));
        };

        _webView.PreviewKeyDown += (s, e) =>
        {
            if (e.KeyCode == System.Windows.Forms.Keys.F11)
            {
                e.IsInputKey = true;
                ToggleFullscreen();
            }
        };

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
                        try { System.IO.File.Delete(f); } catch { }
            }
            Console.WriteLine("[cache] WebView2 cache cleared");
        } catch { }

        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        _webView.CoreWebView2.Settings.IsStatusBarEnabled            = false;
        _webView.CoreWebView2.Settings.AreDevToolsEnabled            = true;
        _webView.CoreWebView2.Settings.AreHostObjectsAllowed         = true;
        _webView.CoreWebView2.WebMessageReceived += OnWebMessage;

        _sensor.Start();

        // Update check: requested by nav.js when a page loads (cached after the first check)

        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "vexis.local", AppDir, CoreWebView2HostResourceAccessKind.Allow);
        _webView.CoreWebView2.Navigate("https://vexis.local/home.html");

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
                    _pollTimer.Tick += OnPollTick;
                    _pollTimer.Start();
                    await RevealWindow();
                    return;
                }
            }
            catch (Exception ex) { Console.WriteLine($"[startup] {ex.Message}"); }
        }
        await RevealWindow(); // page never reported ready — show the window anyway
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
                canInstall = newer && r!.InstallerUrl != null,
                notes    = r?.Notes is { Length: > 1200 } n ? n[..1200] + "…" : r?.Notes,
                url      = UpdateManager.ReleasesUrl
            };
            _updateJson = JsonSerializer.Serialize(info, _json);
            RunScriptEverywhere($"typeof navUpdateInfo==='function'&&navUpdateInfo({_updateJson})");
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
        if (await UpdateManager.DownloadAndRunAsync(r, AppVersionText, Report))
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
                lastPresetIdx = _config.LastPresetIdx, appVersion = AppVersionText,
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
            if (_webView.CoreWebView2 == null) return;
            var data = _sensor.GetLatestData();
            _fans.Update(data, _config);
            CheckTempAlerts(data);

            data.fans = _fans.GetSnapshot();
            _lastData = data;
            _recorder.Append(data);
            data.recording = _recorder.Status;
            lock (_inbox) data.alerts = _inbox.Count > 0 ? _inbox.ToList() : null;

            string json = JsonSerializer.Serialize(data, _json);

            await _webView.CoreWebView2.ExecuteScriptAsync(
                $"typeof receiveData==='function'&&receiveData({json})");

            foreach (var kv in _popouts.ToList())
            {
                if (!kv.Value.IsDisposed) await kv.Value.PushData(json);
                else _popouts.Remove(kv.Key);
            }

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
            Console.WriteLine($"[msg] {raw[..Math.Min(80,raw.Length)]}");
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
                        Invoke(() => _webView.CoreWebView2?.ExecuteScriptAsync(
                            $"typeof updateLogs==='function'&&updateLogs({logJson})"));
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

                case "recordStop":
                    _recorder.Stop();
                    PushRecordings();
                    break;

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
                            Invoke(() => _webView.CoreWebView2?.ExecuteScriptAsync(
                                $"typeof receiveData==='function'&&receiveData({snapJson})"));
                        }
                        catch { }
                    });
                    break;

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
                    if (msg.url != null)
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        { FileName = msg.url, UseShellExecute = true });
                    break;

                case "openrgbProxy":
                    Console.WriteLine($"[rgb] proxy: {msg.method} {msg.path}");
                    _ = Task.Run(async () =>
                    {
                        string cid2 = msg.callId ?? "0";
                        var (ok, json) = await OpenRgbProxyAsync(msg);
                        string payload = System.Text.Json.JsonSerializer.Serialize(json);
                        Invoke(() => _webView.CoreWebView2?.ExecuteScriptAsync(
                            $"typeof orgbProxyResult==='function'&&orgbProxyResult('{cid2}',{(ok ? "true" : "false")},{payload})"));
                    });
                    break;

                case "killOpenRGB":
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName("OpenRGB"))
                        try { p.Kill(); Console.WriteLine($"[rgb] Killed OpenRGB PID {p.Id}"); } catch { }
                    _orgbClient?.ResetFailedDevices();
                    _orgbClient = null;
                    break;

                case "checkOpenRGB":
                {
                    bool isRunning = System.Diagnostics.Process.GetProcessesByName("OpenRGB").Length > 0;
                    string cid3 = msg.callId ?? "0";
                    string runJson = isRunning ? "{\"running\":true}" : "{\"running\":false}";
                    string runEsc  = System.Text.Json.JsonSerializer.Serialize(runJson);
                    Invoke(() => _webView.CoreWebView2?.ExecuteScriptAsync(
                        $"typeof orgbProxyResult==='function'&&orgbProxyResult('{cid3}',true,{runEsc})"));
                    break;
                }

                case "launchOpenRGB":
                    var existing = System.Diagnostics.Process.GetProcessesByName("OpenRGB");
                    if (existing.Length > 0)
                    {
                        Console.WriteLine($"[rgb] OpenRGB already running (PID {existing[0].Id}), not launching again");
                        break;
                    }
                    _sensor.PausePoll(5000);
                    Console.WriteLine("[rgb] Paused sensor polling for OpenRGB launch");
                    var orgbPaths = new[]
                    {
                        Path.Combine(AppDir, "OpenRGB.exe"),
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OpenRGB.exe"),
                        @"C:\Program Files\OpenRGB\OpenRGB.exe",
                        @"C:\Program Files (x86)\OpenRGB\OpenRGB.exe",
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRGB", "OpenRGB.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenRGB", "OpenRGB.exe"),
                        @"C:\OpenRGB\OpenRGB.exe",
                        @"C:\Tools\OpenRGB\OpenRGB.exe",
                    };
                    string? orgbExe = orgbPaths.FirstOrDefault(File.Exists);
                    if (orgbExe != null)
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        { FileName = orgbExe, Arguments = "--server", UseShellExecute = true });
                        Console.WriteLine($"[rgb] Launched OpenRGB --server from {orgbExe}");
                    }
                    else
                    {
                        try {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            { FileName = "OpenRGB.exe", Arguments = "--server", UseShellExecute = true });
                            Console.WriteLine("[rgb] Launched OpenRGB --server via PATH");
                        }
                        catch {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            { FileName = "https://openrgb.org", UseShellExecute = true });
                        }
                    }
                    break;

                case "launchOpenRGBGui":
                    foreach (var p2 in System.Diagnostics.Process.GetProcessesByName("OpenRGB"))
                        try { p2.Kill(); Console.WriteLine($"[rgb] Killed OpenRGB PID {p2.Id} for GUI mode"); } catch { }
                    _orgbClient = null;
                    System.Threading.Thread.Sleep(800);
                    var orgbGuiPaths = new[]
                    {
                        @"C:\Program Files\OpenRGB\OpenRGB.exe",
                        @"C:\Program Files (x86)\OpenRGB\OpenRGB.exe",
                        Path.Combine(AppDir, "OpenRGB.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRGB", "OpenRGB.exe"),
                        @"C:\OpenRGB\OpenRGB.exe",
                    };
                    string? orgbGuiExe = orgbGuiPaths.FirstOrDefault(File.Exists);
                    if (orgbGuiExe != null)
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        { FileName = orgbGuiExe, UseShellExecute = true });
                        Console.WriteLine($"[rgb] Launched OpenRGB GUI from {orgbGuiExe}");
                    }
                    break;

                case "fanSetManual":
                    if (msg.fanName != null) _fans.SetManual(msg.fanName, msg.value ?? 50f);
                    break;
                case "fanSetAuto":
                    if (msg.fanName != null) _fans.SetAuto(msg.fanName);
                    break;
                case "fanSetCurveMode":
                    if (msg.fanName != null) _fans.SetCurveMode(msg.fanName, msg.enabled ?? false);
                    break;
                case "saveFanCurve":
                    if (msg.fanCurve != null)
                    { _config.UpsertCurve(msg.fanCurve); _fans.SetCurveMode(msg.fanCurve.FanName, msg.fanCurve.Enabled); }
                    break;

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
                case "close":    Invoke(() => Hide()); break;
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

    private void OpenPopout(string page)
    {
        if (_popouts.TryGetValue(page, out var existing) && !existing.IsDisposed)
        { existing.Activate(); return; }
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
                    catch { }
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
            try { _orgbClient?.Dispose(); } catch { } // don't leak the old socket
            _orgbClient = null;
            Console.WriteLine($"[rgb] proxy error: {ex.Message}");
            return (false, ex.Message);
        }
        finally { _orgbLock.Release(); }
    }

    private void SetupTray()
    {
        _trayMenu.Items.Clear();
        _trayMenu.Items.Add("Show", null, (_, _) => ShowDashboard());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Exit", null, (_, _) => { _tray.Visible = false; Application.Exit(); });
        Icon? appIcon = null;
        try {
            string icoPath = Path.Combine(AppDir, "vexis.ico");
            if (File.Exists(icoPath)) appIcon = new Icon(icoPath);
        } catch { }
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

    private void ShowDashboard() { Show(); WindowState = FormWindowState.Normal; Activate(); StopBlink(); }

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
    private void OnResize(object? s, EventArgs e) { if (WindowState==FormWindowState.Minimized) Hide(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _fans?.RestoreAll(); } catch { }
            _recorder.Dispose();
            _blinkTimer.Dispose();
            _pollTimer.Dispose(); _sensor?.Dispose(); _tray.Dispose(); _webView.Dispose();
            foreach (var p in _popouts.Values) if (!p.IsDisposed) p.Dispose();
        }
        base.Dispose(disposing);
    }
}

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
}
