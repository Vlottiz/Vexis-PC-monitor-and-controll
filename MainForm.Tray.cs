using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;
using System.Text.Json.Serialization;

// Tray icon and menu, fan profile from the tray, alert inbox and the blinking tray icon
public partial class MainForm
{
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
}
