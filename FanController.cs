using LibreHardwareMonitor.Hardware;
using System.Management;

// ─── Fan info sent to dashboard ────────────────────────────────────────────────
public class FanInfo
{
    public string  name       { get; set; } = "";
    public float   rpm        { get; set; }
    public float   pct        { get; set; }     // current control %
    public bool    hasControl { get; set; }
    public bool    curveActive{ get; set; }
    public string  mode       { get; set; } = "auto";  // auto | manual | curve
    public string? healthStatus { get; set; }
}

// ─── Internal fan state ────────────────────────────────────────────────────────
internal class FanEntry
{
    public string   Name         { get; set; } = "";
    public ISensor  RpmSensor    { get; set; } = null!;
    public ISensor? CtrlSensor   { get; set; }
    public string   Mode         { get; set; } = "auto";   // auto | manual | curve
    public float    ManualPct    { get; set; } = 50f;
    public string?  HealthStatus { get; set; }

    // For health check
    public List<(float SetPct, float MeasuredRpm)> HealthReadings { get; set; } = new();
}

// ─── FanController ─────────────────────────────────────────────────────────────
public class FanController
{
    private readonly List<FanEntry> _fans = new();
    private bool _discovered;

    // WMI fallback fan names — populated when LHM finds no motherboard fan sensors
    // (MSI X870E TOMAHAWK's Nuvoton SuperIO chip is not yet supported by LHM)
    private readonly List<string> _wmiFanNames = new();

    // ─── Discovery ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Scan all hardware for fan + control sensor pairs.
    /// Call once after LHM Computer is open and updated.
    /// </summary>
    public void Discover(IEnumerable<IHardware> hardware)
    {
        _fans.Clear();

        // Flatten top-level hardware + all SubHardware so we catch SuperIO chips.
        // Motherboard fan headers (CPU_FAN, SYS_FAN1, etc.) live on a SuperIO
        // sub-hardware node under the Motherboard — skipping SubHardware means
        // only the GPU fan (which is on the GPU hardware directly) is ever found.
        var allHw = hardware
            .SelectMany(hw => new[] { hw }.Concat(hw.SubHardware))
            .ToList();

        Console.WriteLine($"[fans] Scan: {allHw.Count(h => h.Sensors.Any(s => s.SensorType == SensorType.Fan))} hw object(s) with fan sensors");
        foreach (var h in allHw)
        {
            int fc = h.Sensors.Count(s => s.SensorType == SensorType.Fan);
            if (fc > 0)
                Console.WriteLine($"[fans]   [{h.HardwareType}] {h.Name} — {fc} fan sensor(s)");
        }

        foreach (var hw in allHw)
        {
            // Fan RPM sensors
            var fanSensors  = hw.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
            // Control sensors (0–100%)
            var ctrlSensors = hw.Sensors.Where(s => s.SensorType == SensorType.Control).ToList();

            foreach (var fan in fanSensors)
            {
                // Try to match a control sensor by name similarity
                ISensor? ctrl = null;
                foreach (var c in ctrlSensors)
                {
                    string fn = fan.Name.ToLower().Replace("fan", "").Replace("#", "").Trim();
                    string cn = c.Name.ToLower().Replace("fan", "").Replace("#", "").Trim();
                    if (fn == cn || fan.Name == c.Name ||
                        fan.Name.Replace("Fan", "").Trim() == c.Name.Replace("Fan", "").Trim())
                    {
                        ctrl = c;
                        break;
                    }
                }

                // Fallback: match by index if names don't align
                if (ctrl == null)
                {
                    int idx = fanSensors.IndexOf(fan);
                    if (idx < ctrlSensors.Count)
                        ctrl = ctrlSensors[idx];
                }

                _fans.Add(new FanEntry
                {
                    Name      = fan.Name,
                    RpmSensor = fan,
                    CtrlSensor= ctrl
                });

                Console.WriteLine($"[fans] {fan.Name} — control: {(ctrl != null ? ctrl.Name : "none")}");
            }
        }

        _discovered = true;
        Console.WriteLine($"[fans] Discovered {_fans.Count} fan(s) total.");

        // ── WMI fallback for boards whose SuperIO LHM doesn't support ──────────
        // MSI X870E TOMAHAWK uses a Nuvoton chip not yet in LHM — motherboard fan
        // headers show up with zero sensors. Win32_Fan gives read-only RPM data
        // (no software control) but is better than showing nothing.
        bool hasMotherboardFans = _fans.Any(f =>
            f.RpmSensor.Hardware.HardwareType != HardwareType.GpuAmd &&
            f.RpmSensor.Hardware.HardwareType != HardwareType.GpuNvidia &&
            f.RpmSensor.Hardware.HardwareType != HardwareType.GpuIntel);

        if (!hasMotherboardFans)
        {
            try
            {
                _wmiFanNames.Clear();
                using var q = new ManagementObjectSearcher("SELECT Name FROM Win32_Fan");
                foreach (ManagementObject obj in q.Get())
                {
                    string name = obj["Name"]?.ToString() ?? "Fan";
                    _wmiFanNames.Add(name);
                    Console.WriteLine($"[fans] WMI fallback fan found: {name}");
                }
                if (_wmiFanNames.Count > 0)
                    Console.WriteLine($"[fans] WMI fallback active for {_wmiFanNames.Count} fan(s) — read-only, no curve/manual control.");
                else
                    Console.WriteLine("[fans] WMI Win32_Fan returned no fans — board uses proprietary SDK (MSI Center required for non-GPU fans).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[fans] WMI fan fallback failed: {ex.Message}");
            }
        }
    }

    // ─── Per-cycle update ──────────────────────────────────────────────────────
    /// <summary>
    /// Call every poll cycle. Applies curve speeds if a curve is active.
    /// </summary>
    public void Update(SensorData sensorData, AppConfig config)
    {
        if (!_discovered) return;

        foreach (var fan in _fans)
        {
            if (fan.Mode != "curve") continue;

            var curve = config.GetCurve(fan.Name);
            if (curve == null || !curve.Enabled || curve.Points.Count == 0) continue;

            float temp = curve.TempSource switch
            {
                "ccd0" => sensorData.ccd0_temp ?? sensorData.cpu_temp ?? 50f,
                "ccd1" => sensorData.ccd1_temp ?? sensorData.cpu_temp ?? 50f,
                "gpu"  => sensorData.gpu_temp  ?? 50f,
                _      => sensorData.cpu_temp  ?? 50f
            };

            int targetPct = AppConfig.InterpolateCurve(curve.Points, temp);

            fan.CtrlSensor?.Control?.SetSoftware(targetPct);
        }
    }

    // ─── Fan control ───────────────────────────────────────────────────────────
    public void SetManual(string fanName, float pct)
    {
        var fan = Find(fanName);
        if (fan == null) return;
        fan.Mode = "manual";
        fan.ManualPct = pct;
        fan.CtrlSensor?.Control?.SetSoftware(pct);
    }

    public void SetAuto(string fanName)
    {
        var fan = Find(fanName);
        if (fan == null) return;
        fan.Mode = "auto";
        fan.CtrlSensor?.Control?.SetDefault();
    }

    public void SetCurveMode(string fanName, bool enabled)
    {
        var fan = Find(fanName);
        if (fan == null) return;
        fan.Mode = enabled ? "curve" : "auto";
        if (!enabled)
            fan.CtrlSensor?.Control?.SetDefault();
    }

    // ─── Health check ──────────────────────────────────────────────────────────
    /// <summary>
    /// Runs a brief health check: ramps fan through 30%, 60%, 100%, back to auto.
    /// Returns results after ~8 seconds. Call on a background thread.
    /// </summary>
    public FanHealthResult RunHealthCheck(string fanName)
    {
        var fan = Find(fanName);
        if (fan == null)
            return new FanHealthResult { FanName = fanName, Status = "NOT_FOUND", Message = "Fan not found." };

        if (fan.CtrlSensor?.Control == null)
            return new FanHealthResult { FanName = fanName, Status = "NO_CONTROL", Message = "Fan has no software control — BIOS controlled only." };

        var readings = new List<(int SetPct, float MeasuredRpm)>();

        // Test at 30%, 60%, 100%
        foreach (int pct in new[] { 30, 60, 100 })
        {
            fan.CtrlSensor.Control.SetSoftware(pct);
            Thread.Sleep(2000);  // Wait for fan to spin up
            float rpm = fan.RpmSensor.Value ?? 0f;
            readings.Add((pct, rpm));
            Console.WriteLine($"[health] {fanName} @ {pct}% → {rpm:F0} RPM");
        }

        // Restore auto
        fan.CtrlSensor.Control.SetDefault();
        fan.Mode = "auto";

        // Evaluate
        bool responding = readings.All(r => r.MeasuredRpm > 100);
        bool scaling    = readings[2].MeasuredRpm > readings[0].MeasuredRpm * 1.2f;
        bool stalling   = readings.Any(r => r.MeasuredRpm < 50 && r.SetPct >= 30);

        string status, message;

        if (!responding || stalling)
        {
            status  = "FAIL";
            message = "Fan not responding or stalling. Check connection.";
        }
        else if (!scaling)
        {
            status  = "WARN";
            message = "Fan responds but doesn't scale with control — may be BIOS-controlled.";
        }
        else
        {
            status  = "PASS";
            message = $"Fan healthy. Range: {readings[0].MeasuredRpm:F0}–{readings[2].MeasuredRpm:F0} RPM.";
        }

        fan.HealthStatus = status;

        return new FanHealthResult
        {
            FanName  = fanName,
            Status   = status,
            Message  = message,
            Readings = readings.Select(r => new HealthReading { Pct = r.SetPct, Rpm = r.MeasuredRpm }).ToList()
        };
    }

    // ─── Data snapshot for dashboard ──────────────────────────────────────────
    public List<FanInfo> GetSnapshot()
    {
        var result = _fans.Select(f => new FanInfo
        {
            name        = f.Name,
            rpm         = f.RpmSensor.Value ?? 0f,
            pct         = f.CtrlSensor?.Value ?? 0f,
            // hasControl = true only if LHM has a control sensor AND the fan
            // is not on a GPU — AMD/Nvidia drivers block LHM from overriding
            // GPU fans, so showing controls for them is misleading.
            hasControl  = f.CtrlSensor?.Control != null &&
                          f.RpmSensor.Hardware.HardwareType != HardwareType.GpuAmd &&
                          f.RpmSensor.Hardware.HardwareType != HardwareType.GpuNvidia &&
                          f.RpmSensor.Hardware.HardwareType != HardwareType.GpuIntel,
            curveActive = f.Mode == "curve",
            mode        = f.Mode,
            healthStatus= f.HealthStatus
        }).ToList();

        // Append WMI fallback fans if LHM found none from the motherboard
        if (_wmiFanNames.Count > 0)
        {
            try
            {
                using var q = new ManagementObjectSearcher(
                    "SELECT Name, DesiredSpeed FROM Win32_Fan");
                int idx = 0;
                foreach (ManagementObject obj in q.Get())
                {
                    string name = idx < _wmiFanNames.Count ? _wmiFanNames[idx] : $"Fan {idx+1}";
                    float rpm = 0f;
                    try { rpm = Convert.ToSingle(obj["DesiredSpeed"]); } catch { }
                    result.Add(new FanInfo
                    {
                        name       = name,
                        rpm        = rpm,
                        pct        = 0f,
                        hasControl = false,   // WMI fans are read-only
                        mode       = "auto",
                    });
                    idx++;
                }
            }
            catch { /* WMI unavailable — skip silently */ }
        }

        return result;
    }

    public bool IsDiscovered => _discovered;
    public List<string> FanNames => _fans.Select(f => f.Name).ToList();

    private FanEntry? Find(string name) =>
        _fans.FirstOrDefault(f => f.Name == name);
}

// ─── Health check result ───────────────────────────────────────────────────────
public class FanHealthResult
{
    public string           FanName  { get; set; } = "";
    public string           Status   { get; set; } = "";  // PASS | WARN | FAIL | NO_CONTROL | NOT_FOUND
    public string           Message  { get; set; } = "";
    public List<HealthReading> Readings { get; set; } = new();
}

public class HealthReading
{
    public int   Pct { get; set; }
    public float Rpm { get; set; }
}
