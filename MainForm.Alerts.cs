using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;
using System.Text.Json.Serialization;

// Temperature alerts and the tray notification queue
public partial class MainForm
{
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
}
