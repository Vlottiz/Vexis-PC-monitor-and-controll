using System.Drawing;
using System.Drawing.Drawing2D;

/// <summary>
/// Loading screen shown while Vexis checks drivers and starts the sensors.
///
/// It runs on its own UI thread, so the animation keeps moving while the main
/// window's thread is busy opening the hardware.
///
/// CHANGING THE GRAPHICS
///   The picture is splash.gif next to Vexis.exe (animated GIFs play; splash.png
///   also works if there is no splash.gif). Replace the file and restart Vexis —
///   no rebuild needed. To ship it with the installer, replace splash.gif in the
///   project folder and run build-installer.bat.
///   Best size: 440 × 220 px (other sizes are scaled to fit, keeping proportions).
///   Colours, window size and text are the constants below.
///   If neither file exists, a built-in spinning ring is drawn instead.
/// </summary>
public sealed class SplashForm : Form
{
    // ── Look (change freely) ─────────────────────────────────────────────────
    private static readonly Color Background = Color.FromArgb(10, 8, 8);
    private static readonly Color Accent     = Color.FromArgb(255, 204, 0);   // title, ring, border
    private static readonly Color TextColor  = Color.FromArgb(170, 119, 0);   // status line
    private static readonly Color OkColor    = Color.FromArgb(68, 170, 102);
    private static readonly Color WarnColor  = Color.FromArgb(255, 170, 0);
    private static readonly Color ErrorColor = Color.FromArgb(255, 85, 85);
    private const int WindowWidth = 480, WindowHeight = 330, ImageHeight = 220;

    private readonly PictureBox _picture = new();
    private readonly Label      _status  = new();
    private readonly Label      _checks  = new();
    private readonly System.Windows.Forms.Timer _spin = new() { Interval = 30 };
    private float _angle;
    private bool  _hasImage;
    private readonly List<(string Text, Color Color)> _lines = new();

    private SplashForm(string version)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition   = FormStartPosition.CenterScreen;
        ClientSize      = new Size(WindowWidth, WindowHeight);
        BackColor       = Background;
        ShowInTaskbar   = true;
        TopMost         = false;
        Text            = "Vexis — starting";
        DoubleBuffered  = true;
        try { Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "vexis.ico")); } catch { }

        _picture.SetBounds(20, 16, WindowWidth - 40, ImageHeight);
        _picture.SizeMode  = PictureBoxSizeMode.Zoom;
        _picture.BackColor = Background;
        foreach (var name in new[] { "splash.gif", "splash.png" })
        {
            string file = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(file)) continue;
            try { _picture.Image = Image.FromFile(file); _hasImage = true; break; }
            catch (Exception ex) { Console.WriteLine($"[splash] {name} could not be loaded: {ex.Message}"); }
        }
        if (!_hasImage)
        {
            _picture.Paint += PaintFallback;
            _spin.Tick += (_, _) => { _angle = (_angle + 8) % 360; _picture.Invalidate(); };
            _spin.Start();
        }
        Controls.Add(_picture);

        _status.SetBounds(20, 16 + ImageHeight + 8, WindowWidth - 40, 20);
        _status.ForeColor = TextColor;
        _status.Font      = new Font("Consolas", 9.5f);
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Text      = $"Starting Vexis v{version}…";
        Controls.Add(_status);

        _checks.SetBounds(40, 16 + ImageHeight + 30, WindowWidth - 80, WindowHeight - ImageHeight - 50);
        _checks.Font      = new Font("Consolas", 8.5f);
        _checks.ForeColor = TextColor;
        _checks.Paint    += PaintChecks;
        Controls.Add(_checks);

        Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(90, Accent), 1);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        };
    }

    // Built-in animation when there is no splash image: title + spinning arc
    private void PaintFallback(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var box = _picture.ClientRectangle;
        using var title = new Font("Consolas", 30f, FontStyle.Bold);
        using var brush = new SolidBrush(Accent);
        var fmt = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString("VEXIS", title, brush, new RectangleF(0, 28, box.Width, 60), fmt);
        using var sub = new Font("Consolas", 9f);
        using var subBrush = new SolidBrush(TextColor);
        g.DrawString("HARDWARE MONITORING", sub, subBrush, new RectangleF(0, 82, box.Width, 20), fmt);

        int r = 26, cx = box.Width / 2, cy = 150;
        using var track = new Pen(Color.FromArgb(50, Accent), 4);
        using var arc   = new Pen(Accent, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(track, cx - r, cy - r, r * 2, r * 2);
        g.DrawArc(arc, cx - r, cy - r, r * 2, r * 2, _angle, 100);
    }

    private void PaintChecks(object? sender, PaintEventArgs e)
    {
        e.Graphics.Clear(Background);
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float y = 0;
        lock (_lines)
            foreach (var (text, color) in _lines.TakeLast(4))
            {
                TextRenderer.DrawText(e.Graphics, text, _checks.Font, new Point(0, (int)y), color);
                y += _checks.Font.Height + 1;
            }
    }

    // ── Thread-safe API used by the startup code ─────────────────────────────
    private static SplashForm? _current;

    /// <summary>Shows the loading screen on its own thread and returns once it is up.</summary>
    public static void ShowSplash(string version)
    {
        using var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            var f = new SplashForm(version);
            f.Shown += (_, _) => ready.Set();
            _current = f;
            Application.Run(f);
        }) { IsBackground = true, Name = "Splash" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait(3000);
    }

    public static void SetStatus(string text) => Post(f => f._status.Text = text);

    /// <summary>Adds a result line (level: "ok", "warn" or "error").</summary>
    public static void AddCheck(string level, string text) => Post(f =>
    {
        var (mark, color) = level switch
        {
            "error" => ("✗", ErrorColor),
            "warn"  => ("!", WarnColor),
            _       => ("✓", OkColor)
        };
        lock (f._lines) f._lines.Add(($"{mark} {text}", color));
        f._checks.Invalidate();
    });

    public static void CloseSplash() => Post(f => { f._spin.Stop(); f.Close(); });

    private static void Post(Action<SplashForm> action)
    {
        var f = _current;
        if (f == null || f.IsDisposed || !f.IsHandleCreated) return;
        try { f.BeginInvoke(() => { if (!f.IsDisposed) action(f); }); } catch { }
    }
}
