using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;
using System.Text.Json.Serialization;

// ─── Main window ────────────────────────────────────────────────────────────────
// The hub of the app: owns the sensor service, fan controller, tray icon and the WebView2
// that shows the pages. Every 250 ms OnPollTick sends one JSON snapshot to every open page;
// pages send commands back through HandleMessage, which hands each to one handler per area
// (MainForm.Messages.cs). The other MainForm.*.cs files hold one concern each.
// docs/ARCHITECTURE.md has the bigger picture.
public partial class MainForm : Form
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
                SysTools.OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/");
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
        _fans.StartWatchdog();         // full speed if fan updates ever stop (see FanController)
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

            // Each area handles its own message types (MainForm.Messages.cs); the first that knows
            // the type handles it. A page message no handler knows is logged.
            if (HandleWindowMessage(msg) || HandleSettingsMessage(msg) || HandleFileMessage(msg) ||
                HandleProcessMessage(msg) || HandleSecurityMessage(msg) || HandleRgbMessage(msg) ||
                HandleFanMessage(msg)) return;
            Console.WriteLine($"[msg] unknown: {msg.type}");
        }
        catch (Exception ex) { Console.WriteLine($"[msg] error: {ex.Message}"); }
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
