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

    public MainForm()
    {
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
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        _config = AppConfig.Load();
        _sensor = new SensorService(_config);
        _fans   = new FanController();
        _fans.Discover(_sensor.AllHardware);
        SetupTray();

        var webViewOpts = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions(
            additionalBrowserArguments: "--disable-web-security --allow-running-insecure-content --allow-file-access-from-files");

        var webViewEnv = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Vexis", "WebView2Cache"),
            options: webViewOpts);
        await _webView.EnsureCoreWebView2Async(webViewEnv);

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

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(5000);
                using var http = new System.Net.Http.HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", $"Vexis/{AppVersionText}");
                http.Timeout = TimeSpan.FromSeconds(8);
                var json = await http.GetStringAsync(
                    "https://api.github.com/repos/Vlottiz/vexis/releases/latest");
                var doc  = System.Text.Json.JsonDocument.Parse(json);
                var tag  = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                if (Version.TryParse(tag.TrimStart('v', 'V'), out var latest) &&
                    latest > new Version(AppVersionText))
                {
                    Console.WriteLine($"[update] New version available: {tag}");
                    Invoke(() => _webView.CoreWebView2?.ExecuteScriptAsync(
                        $"typeof navSetUpdateAvailable==='function'&&navSetUpdateAvailable('{tag}')"));
                }
                else Console.WriteLine($"[update] Up to date (running v{AppVersionText}, latest {tag})");
            }
            catch (Exception ex) { Console.WriteLine($"[update] Check failed: {ex.Message}"); }
        });

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
                    return;
                }
            }
            catch (Exception ex) { Console.WriteLine($"[startup] {ex.Message}"); }
        }
    }

    // Sends driver / Windows protection status to the Security page (main window + popouts)
    private void PushSecurityStatus()
    {
        string json   = JsonSerializer.Serialize(DriverSetup.GetSecurityStatus(), _json);
        string script = $"typeof onSecurityStatus==='function'&&onSecurityStatus({json})";
        BeginInvoke(() =>
        {
            _webView.CoreWebView2?.ExecuteScriptAsync(script);
            foreach (var p in _popouts.Values)
                if (!p.IsDisposed) _ = p.ExecuteScriptAsync(script);
        });
    }

    private async Task SendConfigViaScript()
    {
        try
        {
            var payload = new
            {
                type = "config", colors = _config.Colors, settings = _config.Settings,
                colorProfiles = _config.ColorProfiles, fanCurves = _config.FanCurves,
                lastPresetIdx = _config.LastPresetIdx, appVersion = AppVersionText
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

            data.fans = _fans.GetSnapshot();

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
        try
        {
            string raw = e.TryGetWebMessageAsString();
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

                case "getSecurityStatus":
                    PushSecurityStatus();
                    break;

                case "installPawnIO":
                    _ = Task.Run(() => { DriverSetup.EnsurePawnIo(); PushSecurityStatus(); });
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
                        await _orgbLock.WaitAsync();
                        try
                        {
                            if (_orgbClient == null || !_orgbClient.IsConnected)
                            {
                                _orgbClient?.Dispose();
                                _orgbClient = new OpenRGBClient(msg.port ?? 6742);
                                await _orgbClient.ConnectAsync();
                            }

                            string orgbPath = msg.path ?? "/devices";
                            string jsonResult2;

                            if (orgbPath == "/devices")
                                jsonResult2 = await _orgbClient.GetDevicesJsonAsync();
                            else if (orgbPath.Contains("/leds") && msg.body != null)
                            {
                                var parts2 = orgbPath.Split('/');
                                uint devIdx2 = uint.Parse(parts2[2]);
                                var leds2 = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string,int>>>(msg.body)!;
                                await _orgbClient.SetLedsAsync(devIdx2, leds2);
                                jsonResult2 = "{}";
                            }
                            else if (orgbPath.Contains("/mode") && msg.method == "PUT" && msg.body != null)
                            {
                                var parts2 = orgbPath.Split('/');
                                uint devIdx2 = uint.Parse(parts2[2]);
                                var modeDoc = System.Text.Json.JsonDocument.Parse(msg.body).RootElement;
                                int modeIdx = modeDoc.TryGetProperty("mode", out var mv) ? mv.GetInt32() : 0;
                                byte r = (byte)(modeDoc.TryGetProperty("r", out var rv) ? rv.GetInt32() : 255);
                                byte g = (byte)(modeDoc.TryGetProperty("g", out var gv) ? gv.GetInt32() : 255);
                                byte b = (byte)(modeDoc.TryGetProperty("b", out var bv) ? bv.GetInt32() : 255);
                                await _orgbClient.SetCustomModeAsync(devIdx2);
                                Console.WriteLine($"[orgb] SetCustomMode dev={devIdx2} → entering direct mode");
                                jsonResult2 = "{}";
                            }
                            else { jsonResult2 = "{}"; }

                            string esc2 = System.Text.Json.JsonSerializer.Serialize(jsonResult2);
                            Invoke(() => _webView.CoreWebView2?.ExecuteScriptAsync(
                                $"typeof orgbProxyResult==='function'&&orgbProxyResult('{cid2}',true,{esc2})"));
                        }
                        catch (Exception ex2)
                        {
                            _orgbClient = null;
                            Console.WriteLine($"[rgb] proxy error: {ex2.Message}");
                            string ej2 = System.Text.Json.JsonSerializer.Serialize(ex2.Message);
                            Invoke(() => _webView.CoreWebView2?.ExecuteScriptAsync(
                                $"typeof orgbProxyResult==='function'&&orgbProxyResult('{cid2}',false,{ej2})"));
                        }
                        finally { _orgbLock.Release(); }
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

                        if (msg2.type == "openrgbProxy" && _orgbClient != null && msg2.path != null)
                        {
                            string jsonResult = "{}";
                            try
                            {
                                if (msg2.path == "/devices")
                                    jsonResult = await _orgbClient.GetDevicesJsonAsync();
                                else if (msg2.path.Contains("/leds") && msg2.body != null)
                                {
                                    var parts3 = msg2.path.Split('/');
                                    uint devIdx3 = uint.Parse(parts3[2]);
                                    var leds3 = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string,int>>>(msg2.body)!;
                                    await _orgbClient.SetLedsAsync(devIdx3, leds3);
                                }
                                else if (msg2.path.Contains("/mode") && msg2.method == "PUT" && msg2.body != null)
                                {
                                    var parts3 = msg2.path.Split('/');
                                    uint devIdx3 = uint.Parse(parts3[2]);
                                    await _orgbClient.SetCustomModeAsync(devIdx3);
                                }
                            }
                            catch { }
                            string cid3 = msg2.callId ?? "0";
                            string esc3 = System.Text.Json.JsonSerializer.Serialize(jsonResult);
                            await form!.ExecuteScriptAsync(
                                $"typeof orgbProxyResult==='function'&&orgbProxyResult('{cid3}',true,{esc3})");
                        }
                        else if (msg2.type == "checkOpenRGB")
                        {
                            bool connected = _orgbClient?.IsConnected ?? false;
                            string cid3 = msg2.callId ?? "0";
                            await form!.ExecuteScriptAsync(
                                $"typeof orgbCheckResult==='function'&&orgbCheckResult('{cid3}',{connected.ToString().ToLower()})");
                        }
                    }
                    catch { }
                });
            });
        });
        _popouts[page] = form;
        form.FormClosed += (_, _) => _popouts.Remove(page);
        form.Show();
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
        _tray.Icon = appIcon ?? SystemIcons.Application;
        if (appIcon != null) this.Icon = appIcon;
        _tray.ContextMenuStrip = _trayMenu; _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowDashboard();
    }

    private void ShowDashboard() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void OnClosing(object? s, FormClosingEventArgs e) { if (e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();} }
    private void OnResize(object? s, EventArgs e) { if (WindowState==FormWindowState.Minimized) Hide(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
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
}
