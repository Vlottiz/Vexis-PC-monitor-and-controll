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
    public string  mode       { get; set; } = "auto";  // auto | manual | curve | profile
    public string? healthStatus { get; set; }
    public bool    isGpu      { get; set; }     // graphics-card fan (driver control)
    public bool    failsafe   { get; set; }     // forced to 100% by the GPU over-temperature guard
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

    // Curve state for hysteresis: the speed last set and the temperature it was set at
    public int      CurveSpeed   { get; set; } = -1;
    public float    CurveTemp    { get; set; }
    public int      LastSent     { get; set; } = -1;

    public bool IsPump => Name.Contains("Pump", StringComparison.OrdinalIgnoreCase) ||
                          Name.Contains("AIO", StringComparison.OrdinalIgnoreCase);

    // For health check
    public List<(float SetPct, float MeasuredRpm)> HealthReadings { get; set; } = new();

    public bool IsGpu => RpmSensor.Hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel;
}

// ─── FanController ─────────────────────────────────────────────────────────────
public class FanController
{
    private List<FanEntry> _fans = new();
    private bool _discovered;

    // LHM lists a SuperIO fan only after a successful read. The read is skipped
    // when another program (MSI Center, HWiNFO, ...) holds the ISA bus lock, so
    // motherboard fans can appear after startup — keep looking for them.
    private List<IHardware> _hardware = new();
    private DateTime _startedAt   = DateTime.Now;
    private DateTime _lastRescan  = DateTime.Now;

    // WMI fallback fan names — populated when LHM finds no motherboard fan sensors
    private readonly List<string> _wmiFanNames = new();

    // ─── Discovery ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Scan all hardware for fan + control sensor pairs.
    /// Call once after LHM Computer is open and updated.
    /// </summary>
    public void Discover(IEnumerable<IHardware> hardware)
    {
        _hardware = hardware.ToList();
        var found = new List<FanEntry>();

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

                found.Add(new FanEntry
                {
                    Name      = fan.Name,
                    RpmSensor = fan,
                    CtrlSensor= ctrl
                });

                Console.WriteLine($"[fans] {fan.Name} — control: {(ctrl != null ? ctrl.Name : "none")}");
            }
        }

        _fans = found;
        _discovered = true;
        Console.WriteLine($"[fans] Discovered {_fans.Count} fan(s) total.");

        // ── WMI fallback for boards whose SuperIO LHM doesn't support ──────────
        // If the SuperIO chip can't be read, motherboard fan headers show up with
        // zero sensors. Win32_Fan gives read-only RPM data (no software control)
        // but is better than showing nothing.
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
                    Console.WriteLine("[fans] No motherboard fans yet — will keep checking (close MSI Center / HWiNFO if they stay missing).");
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
    // GPU over-temperature guard: a GPU fan under manual or curve control is forced
    // to 100% when the core or hot spot gets this hot, and released 8 °C below.
    public const float GpuCoreLimit = 90f, GpuHotspotLimit = 100f, GpuGuardRelease = 8f;
    private bool _gpuGuard;

    private AppConfig? _config;

    public void Update(SensorData sensorData, AppConfig config)
    {
        if (!_discovered) return;
        _config = config;
        RescanIfNewFans();

        float gCore = sensorData.gpu?.temp_core ?? sensorData.gpu_temp ?? 0f;
        float gHot  = sensorData.gpu?.temp_hotspot ?? 0f;
        bool wasGuard = _gpuGuard;
        if (!_gpuGuard && (gCore >= GpuCoreLimit || gHot >= GpuHotspotLimit)) _gpuGuard = true;
        else if (_gpuGuard && gCore < GpuCoreLimit - GpuGuardRelease && gHot < GpuHotspotLimit - GpuGuardRelease) _gpuGuard = false;
        if (_gpuGuard != wasGuard)
            Console.WriteLine(_gpuGuard
                ? $"[fans] GPU over-temperature guard ON (core {gCore:F0}°C, hot spot {gHot:F0}°C) — GPU fans at 100%."
                : "[fans] GPU over-temperature guard off — GPU fans back to their setting.");

        string profile = FanProfiles.Normalize(config.FanProfile);
        foreach (var fan in _fans)
        {
            if (fan.CtrlSensor?.Control == null || fan.RpmSensor.Hardware.HardwareType == HardwareType.GpuIntel) continue;
            if (fan.IsGpu && fan.Mode != "auto")
            {
                if (_gpuGuard) { Send(fan, 100); continue; }
                if (wasGuard && fan.Mode == "manual") Send(fan, (int)fan.ManualPct);
            }

            FanCurve? curve = fan.Mode switch
            {
                "profile" => FanProfiles.CurveFor(profile, fan.Name, fan.IsGpu, fan.IsPump),
                "curve"   => config.GetCurve(fan.Name) is { Enabled: true } c && c.Points.Count > 0 ? c : null,
                _         => null
            };
            if (curve == null) continue;
            Send(fan, CurveSpeed(fan, curve, SourceTemp(curve.TempSource, sensorData)));
        }
    }

    private static float SourceTemp(string? source, SensorData d) => source switch
    {
        "ccd0"        => d.ccd0_temp ?? d.cpu_temp ?? 50f,
        "ccd1"        => d.ccd1_temp ?? d.cpu_temp ?? 50f,
        "gpu"         => d.gpu?.temp_core ?? d.gpu_temp ?? 50f,
        "gpu_hotspot" => d.gpu?.temp_hotspot ?? d.gpu_temp ?? 50f,
        "gpu_mem"     => d.gpu?.temp_mem ?? d.gpu_temp ?? 50f,
        _             => d.cpu_temp ?? 50f
    };

    /// <summary>
    /// Curve speed with hysteresis and a minimum speed. Speeding up happens at once;
    /// slowing down waits until the temperature has dropped <c>Hysteresis</c> °C below
    /// the temperature that set the current speed — so a CPU hovering around a curve
    /// point doesn't make the fans rev up and down.
    /// </summary>
    internal static int CurveSpeed(FanEntry fan, FanCurve curve, float temp)
    {
        int target = AppConfig.InterpolateCurve(curve.Points, temp);
        if (fan.CurveSpeed < 0 || target > fan.CurveSpeed || temp <= fan.CurveTemp - Math.Max(0, curve.Hysteresis))
        {
            fan.CurveSpeed = target;
            fan.CurveTemp  = temp;
        }
        return Math.Clamp(Math.Max(fan.CurveSpeed, curve.MinSpeed), 0, 100);
    }

    // Only talk to the hardware when the speed actually changes
    private static void Send(FanEntry fan, int pct)
    {
        if (pct == fan.LastSent) return;
        fan.CtrlSensor?.Control?.SetSoftware(pct);
        fan.LastSent = pct;
    }

    /// <summary>
    /// Applies the fan profile. A preset puts every controllable fan on its preset
    /// curve; "custom" restores each fan's own saved curve (or BIOS / driver auto).
    /// Called at startup, after fans are rediscovered and when the profile changes.
    /// </summary>
    public void ApplyProfile(AppConfig config)
    {
        string profile = FanProfiles.Normalize(config.FanProfile);
        foreach (var fan in _fans)
        {
            if (fan.CtrlSensor?.Control == null || fan.RpmSensor.Hardware.HardwareType == HardwareType.GpuIntel) continue;
            fan.CurveSpeed = -1; fan.LastSent = -1;
            if (profile != "custom") { fan.Mode = "profile"; continue; }
            if (fan.Mode == "manual") { Send(fan, (int)fan.ManualPct); continue; }
            if (config.GetCurve(fan.Name) is { Enabled: true } c && c.Points.Count > 0) fan.Mode = "curve";
            else { fan.Mode = "auto"; try { fan.CtrlSensor.Control.SetDefault(); } catch { } }
        }
        Console.WriteLine($"[fans] Profile: {profile}");
    }

    /// <summary>Changing one fan by hand leaves the preset and goes back to Custom.</summary>
    public void LeavePreset(AppConfig config)
    {
        if (FanProfiles.Normalize(config.FanProfile) == "custom") return;
        config.FanProfile = "custom";
        config.Save();
        foreach (var f in _fans) if (f.Mode == "profile") f.Mode = "auto";
        ApplyProfile(config);
    }

    // Every 5 s for the first 2 minutes, then every 30 s: if LHM now exposes more
    // fan sensors than we know about, discover again (keeping each fan's mode).
    private void RescanIfNewFans()
    {
        var now = DateTime.Now;
        var interval = now - _startedAt < TimeSpan.FromMinutes(2)
            ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30);
        if (now - _lastRescan < interval) return;
        _lastRescan = now;

        try
        {
            int fanSensors = _hardware
                .SelectMany(hw => new[] { hw }.Concat(hw.SubHardware))
                .Sum(h => h.Sensors.Count(x => x.SensorType == SensorType.Fan));
            if (fanSensors <= _fans.Count) return;

            Console.WriteLine($"[fans] {fanSensors - _fans.Count} new fan sensor(s) appeared — rescanning.");
            var previous = _fans.ToDictionary(f => f.Name);
            Discover(_hardware);
            var fresh = new List<FanEntry>();
            foreach (var f in _fans)
                if (previous.TryGetValue(f.Name, out var old))
                {
                    f.Mode = old.Mode;
                    f.ManualPct = old.ManualPct;
                    if (f.Mode == "manual") Send(f, (int)f.ManualPct);
                }
                else fresh.Add(f);
            // Newly found fans join the active profile / their saved curve
            if (_config != null && fresh.Count > 0)
            {
                string profile = FanProfiles.Normalize(_config.FanProfile);
                foreach (var f in fresh)
                {
                    if (f.CtrlSensor?.Control == null || f.RpmSensor.Hardware.HardwareType == HardwareType.GpuIntel) continue;
                    if (profile != "custom") f.Mode = "profile";
                    else if (_config.GetCurve(f.Name) is { Enabled: true } c && c.Points.Count > 0) f.Mode = "curve";
                }
            }
        }
        catch (Exception ex)
        {
            // Sensor lists can change under us while LHM activates sensors — try next time
            Console.WriteLine($"[fans] Rescan skipped: {ex.Message}");
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
        fan.LastSent = (int)pct;
    }

    public void SetAuto(string fanName)
    {
        var fan = Find(fanName);
        if (fan == null) return;
        fan.Mode = "auto";
        fan.LastSent = -1;
        fan.CtrlSensor?.Control?.SetDefault();
    }

    public void SetCurveMode(string fanName, bool enabled)
    {
        var fan = Find(fanName);
        if (fan == null) return;
        fan.Mode = enabled ? "curve" : "auto";
        fan.CurveSpeed = -1; fan.LastSent = -1;
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
            // GPU fans are set through the graphics driver (NVAPI / ADL). Intel Arc
            // exposes no fan control, so it only gets a control sensor on NVIDIA/AMD.
            hasControl  = f.CtrlSensor?.Control != null &&
                          f.RpmSensor.Hardware.HardwareType != HardwareType.GpuIntel,
            isGpu       = f.IsGpu,
            failsafe    = f.IsGpu && _gpuGuard && f.Mode != "auto",
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

    /// <summary>Hands every fan Vexis controls back to the BIOS / graphics driver.
    /// Called on exit so a GPU fan is never left at a fixed speed.</summary>
    public void RestoreAll()
    {
        foreach (var f in _fans)
        {
            if (f.Mode == "auto") continue;
            try { f.CtrlSensor?.Control?.SetDefault(); } catch { }
            f.Mode = "auto";
        }
        Console.WriteLine("[fans] Fan control handed back to BIOS / driver.");
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
