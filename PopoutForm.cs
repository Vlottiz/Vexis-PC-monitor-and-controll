using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;
using System.Text.Json.Serialization;

public class PopoutForm : Form
{
    private readonly WebView2        _webView   = new();
    private readonly AppConfig       _config;
    private readonly string          _pageName;
    private readonly Action<string>? _onMessage; // routes popout messages back to MainForm
    private bool                     _ready     = false;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy   = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling         = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;

    private static readonly Dictionary<string, (string file, string title, System.Drawing.Size size)> PageMap = new()
    {
        ["home"]        = ("home.html",     "HOME",          new(500, 700)),
        ["performance"] = ("index.html",    "PERFORMANCE",   new(420, 900)),
        ["gpu"]         = ("gpu.html",      "GPU",           new(520, 800)),
        ["memory"]      = ("memory.html",   "MEMORY",        new(420, 700)),
        ["temps"]       = ("temps.html",    "TEMPERATURES",  new(600, 700)),
        ["fans"]        = ("fans.html",     "FAN CONTROL",   new(500, 700)),
        ["rgb"]         = ("rgb.html",      "RGB CONTROL",   new(500, 600)),
        ["info"]        = ("info.html",     "INFO",          new(480, 600)),
        ["security"]    = ("security.html", "SECURITY",      new(480, 700)),
    };

    // 2-arg constructor (no message routing needed)
    public PopoutForm(string pageName, AppConfig config)
        : this(pageName, config, null) { }

    // 3-arg constructor — onMessage callback routes popout WebMessages to MainForm
    public PopoutForm(string pageName, AppConfig config, Action<string>? onMessage)
    {
        _pageName  = pageName;
        _config    = config;
        _onMessage = onMessage;

        var (_, title, size) = PageMap.GetValueOrDefault(pageName, ("index.html", pageName.ToUpper(), new(500, 800)));

        Text            = $"⊞ INSTANCE — {title}";
        Size            = size;
        MinimumSize     = new System.Drawing.Size(380, 400);
        BackColor       = System.Drawing.Color.Black;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition   = FormStartPosition.CenterScreen;

        _webView.Dock = DockStyle.Fill;
        Controls.Add(_webView);
        Load += OnLoad;
    }

    private async void OnLoad(object? s, EventArgs e)
    {
        var webViewOptsP = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions(
            additionalBrowserArguments: "--disable-web-security --allow-running-insecure-content");
        var webViewEnv = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Vexis", $"WebView2Cache_popout_{_pageName}"),
            options: webViewOptsP);
        await _webView.EnsureCoreWebView2Async(webViewEnv);
        _webView.ZoomFactor = ZoomHelper.Parse(_config.Settings); // start at the main window's zoom
        _webView.ZoomFactorChanged += (_, _) =>
            _webView.CoreWebView2?.ExecuteScriptAsync(ZoomHelper.Script(_webView.ZoomFactor));
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        _webView.CoreWebView2.Settings.IsStatusBarEnabled            = false;
        _webView.CoreWebView2.Settings.AreDevToolsEnabled            = true;
        _webView.CoreWebView2.WebMessageReceived += OnWebMessage;

        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "vexis.local", AppDir, CoreWebView2HostResourceAccessKind.Allow);

        var (file, _, _) = PageMap.GetValueOrDefault(_pageName, ("index.html", "", default));

        // ?popout=1 tells nav.js to show INSTANCE badge instead of nav bar
        _webView.CoreWebView2.Navigate($"https://vexis.local/{file}?popout=1");

        _ = WaitForReady();
    }

    private async Task WaitForReady()
    {
        // Popout creates a fresh WebView2 cache dir which takes longer than the
        // main window — give it a 500ms head start before polling.
        await Task.Delay(500);
        for (int i = 0; i < 80; i++)  // 80 × 250ms + 500ms = 20.5s max
        {
            await Task.Delay(250);
            try
            {
                if (IsDisposed) return;
                // Accept either receiveData (data pages) or onConfigReceived (settings pages)
                var r = await _webView.CoreWebView2.ExecuteScriptAsync(
                    "typeof receiveData === 'function' || typeof onConfigReceived === 'function'");
                if (r == "true")
                {
                    _ready = true;
                    Console.WriteLine($"[popout:{_pageName}] ready after {(i+1)*250+500}ms");
                    await SendConfig();
                    return;
                }
            }
            catch { }
        }
        Console.WriteLine($"[popout:{_pageName}] page ready timeout after 20s");
    }

    private async Task SendConfig()
    {
        try
        {
            var payload = new
            {
                type = "config", colors = _config.Colors, settings = _config.Settings,
                colorProfiles = _config.ColorProfiles, fanCurves = _config.FanCurves,
                appVersion = MainForm.AppVersionText, startWithWindows = MainForm.StartWithWindowsEnabled,
                zoom = Math.Round(_webView.ZoomFactor * 100)
            };
            string json = JsonSerializer.Serialize(payload, _json);
            await _webView.CoreWebView2.ExecuteScriptAsync(
                $"typeof onConfigReceived==='function'&&onConfigReceived({json})");
        }
        catch { }
    }

    private void OnWebMessage(object? s, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string raw = e.TryGetWebMessageAsString();
            // Always handle config requests locally
            if (raw.Contains("\"requestConfig\"")) { _ = SendConfig(); return; }
            // Zoom buttons zoom this window, not the main one
            if (raw.Contains("\"zoom\""))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "zoom")
                {
                    int delta = doc.RootElement.TryGetProperty("delta", out var d) ? d.GetInt32() : 0;
                    ZoomHelper.Change(_webView, delta);
                    return;
                }
            }
            // Route everything else back to MainForm via the callback
            _onMessage?.Invoke(raw);
        }
        catch { }
    }

    // Called by MainForm to push sensor data into the popout page
    public async Task PushData(string sensorJson)
    {
        if (!_ready || IsDisposed) return;
        try
        {
            await _webView.CoreWebView2.ExecuteScriptAsync(
                $"typeof receiveData==='function'&&receiveData({sensorJson})");
        }
        catch { }
    }

    // Called by MainForm to send script results back into the popout (e.g. OpenRGB proxy responses)
    public async Task ExecuteScriptAsync(string script)
    {
        if (!_ready || IsDisposed) return;
        try { await _webView.CoreWebView2.ExecuteScriptAsync(script); }
        catch { }
    }
}
