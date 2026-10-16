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
    public bool    failsafe   { get; set; }     // forced to 100% by the GPU or CPU over-temperature guard
    public int     minPct     { get; set; }     // lowest speed Vexis will set (pumps: FanController.PumpMinPct)
    public bool    isPump     { get; set; }
    public bool    pumpByName { get; set; }     // detected from the name (can't be unmarked)
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
    public bool     Testing      { get; set; }   // health check running: the poll loop leaves it alone

    // A pump gets the 50% floor, the pump curves and pump health-check steps. Detected
    // from the header name, or marked by the user on the Fan Control page.
    public bool PumpByName => Name.Contains("Pump", StringComparison.OrdinalIgnoreCase) ||
                              Name.Contains("AIO", StringComparison.OrdinalIgnoreCase);
    public bool IsPump => PumpByName || FanController.IsMarkedPump(Name);

    // For health check
    public List<(float SetPct, float MeasuredRpm)> HealthReadings { get; set; } = new();

    public bool IsGpu => RpmSensor.Hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel;
}

// ─── FanController ─────────────────────────────────────────────────────────────
public class FanController
{
    private List<FanEntry> _fans = new();
    // The poll (UI thread), the health check (worker thread) and exit all write to the same
    // fan chip. LibreHardwareMonitor isn't thread-safe, so every write takes this lock.
    private static readonly object _hwLock = new();

    // Fans the user marked as pumps (saved in config.PumpFans)
    private static readonly HashSet<string> _markedPumps = new(StringComparer.OrdinalIgnoreCase);
    internal static bool IsMarkedPump(string name) { lock (_markedPumps) return _markedPumps.Contains(name); }
    internal static void LoadMarkedPumps(IEnumerable<string>? names)
    {
        lock (_markedPumps) { _markedPumps.Clear(); foreach (var n in names ?? Array.Empty<string>()) _markedPumps.Add(n); }
    }

    /// <summary>Marks or unmarks a fan as a pump, saves it, and applies the pump floor at once.</summary>
    public void SetPump(string fanName, bool isPump, AppConfig config)
    {
        lock (_markedPumps)
        {
            if (isPump) _markedPumps.Add(fanName); else _markedPumps.Remove(fanName);
            config.PumpFans = _markedPumps.ToList();
        }
        config.Save();
        var fan = Find(fanName);
        if (fan == null) return;
        fan.LastSent = -1; fan.CurveSpeed = -1;
        if (fan.Mode == "manual") { fan.ManualPct = SafeSpeed(fan.ManualPct, fan.IsPump); Send(fan, (int)fan.ManualPct); }
        Console.WriteLine($"[fans] {fanName} {(isPump ? "marked as a pump" : "no longer marked as a pump")}");
    }
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

    // CPU over-temperature guard: case, CPU and pump fans under manual, curve or profile
    // control go to 100% at 95 °C (AMD's limit, a few degrees under Intel's) and are
    // released at 85 °C. Fans on auto are already the BIOS's job.
    public const float CpuLimit = 95f, CpuGuardRelease = 10f;
    private bool _cpuGuard;

    /// <summary>
    /// Guard state with hysteresis: switches on when any reading reaches its limit, and off
    /// only when every reading is <paramref name="release"/> °C under its limit.
    /// </summary>
    internal static bool NextGuard(bool on, float release, params (float temp, float limit)[] readings) =>
        readings.Any(r => r.temp >= (on ? r.limit - release : r.limit));

    private AppConfig? _config;

    public void Update(SensorData sensorData, AppConfig config)
    {
        if (!_discovered) return;
        _config = config;
        RescanIfNewFans();

        float gCore = sensorData.gpu?.temp_core ?? sensorData.gpu_temp ?? 0f;
        float gHot  = sensorData.gpu?.temp_hotspot ?? 0f;
        bool wasGuard = _gpuGuard;
        _gpuGuard = NextGuard(_gpuGuard, GpuGuardRelease, (gCore, GpuCoreLimit), (gHot, GpuHotspotLimit));
        if (_gpuGuard != wasGuard)
            Console.WriteLine(_gpuGuard
                ? $"[fans] GPU over-temperature guard ON (core {gCore:F0}°C, hot spot {gHot:F0}°C) — GPU fans at 100%."
                : "[fans] GPU over-temperature guard off — GPU fans back to their setting.");

        float cpu = sensorData.cpu_temp ?? 0f;
        bool wasCpuGuard = _cpuGuard;
        _cpuGuard = NextGuard(_cpuGuard, CpuGuardRelease, (cpu, CpuLimit));
        if (_cpuGuard != wasCpuGuard)
            Console.WriteLine(_cpuGuard
                ? $"[fans] CPU over-temperature guard ON ({cpu:F0}°C) — case, CPU and pump fans at 100%."
                : "[fans] CPU over-temperature guard off — fans back to their setting.");

        string profile = FanProfiles.Normalize(config.FanProfile);
        foreach (var fan in _fans)
        {
            if (fan.CtrlSensor?.Control == null || fan.RpmSensor.Hardware.HardwareType == HardwareType.GpuIntel) continue;
            if (fan.Testing) continue;
            if (fan.IsGpu && fan.Mode != "auto")
            {
                if (_gpuGuard) { Send(fan, 100); continue; }
                if (wasGuard && fan.Mode == "manual") Send(fan, (int)fan.ManualPct);
            }
            if (!fan.IsGpu && fan.Mode != "auto")
            {
                if (_cpuGuard) { Send(fan, 100); continue; }
                if (wasCpuGuard && fan.Mode == "manual") Send(fan, (int)fan.ManualPct);
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

    /// <summary>Lowest speed Vexis will ever set a pump to. A slowed pump heats the CPU quickly.</summary>
    public const int PumpMinPct = 50;

    /// <summary>
    /// The speed that is actually safe to send: 0–100, never below <see cref="PumpMinPct"/> for a
    /// pump, and a reading that isn't a number becomes 100 (full speed is the safe failure).
    /// </summary>
    internal static int SafeSpeed(float pct, bool isPump)
    {
        if (float.IsNaN(pct)) return 100;
        int p = (int)Math.Round(Math.Clamp(pct, 0f, 100f));
        return isPump ? Math.Max(p, PumpMinPct) : p;
    }

    // Every speed Vexis sets goes through here: made safe, and only sent when it changes
    private static void Send(FanEntry fan, int pct)
    {
        pct = SafeSpeed(pct, fan.IsPump);
        if (pct == fan.LastSent) return;
        lock (_hwLock) fan.CtrlSensor?.Control?.SetSoftware(pct);
        fan.LastSent = pct;
    }

    /// <summary>
    /// Applies the fan profile. A preset puts every controllable fan on its preset
    /// curve; "custom" restores each fan's own saved curve (or BIOS / driver auto).
    /// Called at startup, after fans are rediscovered and when the profile changes.
    /// </summary>
    public void ApplyProfile(AppConfig config)
    {
        LoadMarkedPumps(config.PumpFans);
        string profile = FanProfiles.Normalize(config.FanProfile);
        foreach (var fan in _fans)
        {
            if (fan.CtrlSensor?.Control == null || fan.RpmSensor.Hardware.HardwareType == HardwareType.GpuIntel) continue;
            fan.CurveSpeed = -1; fan.LastSent = -1;
            if (profile != "custom") { fan.Mode = "profile"; continue; }
            if (fan.Mode == "manual") { Send(fan, (int)fan.ManualPct); continue; }
            if (config.GetCurve(fan.Name) is { Enabled: true } c && c.Points.Count > 0) fan.Mode = "curve";
            else { fan.Mode = "auto"; try { lock (_hwLock) fan.CtrlSensor.Control.SetDefault(); } catch (Exception ex) { Console.WriteLine($"[fans] {fan.Name}: couldn't hand control back to the BIOS: {ex.Message}"); } }
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
        fan.ManualPct = SafeSpeed(pct, fan.IsPump);
        fan.LastSent = -1;                      // always send, even if the value is unchanged
        Send(fan, (int)fan.ManualPct);
    }

    public void SetAuto(string fanName)
    {
        var fan = Find(fanName);
        if (fan == null) return;
        fan.Mode = "auto";
        fan.LastSent = -1;
        lock (_hwLock) fan.CtrlSensor?.Control?.SetDefault();
    }

    public void SetCurveMode(string fanName, bool enabled)
    {
        var fan = Find(fanName);
        if (fan == null) return;
        fan.Mode = enabled ? "curve" : "auto";
        fan.CurveSpeed = -1; fan.LastSent = -1;
        if (!enabled)
            lock (_hwLock) fan.CtrlSensor?.Control?.SetDefault();
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

        // Three steps, low to full. Pumps start at their floor, never lower. The poll
        // loop skips the fan while Testing is set, so the two don't fight over it.
        int[] steps = fan.IsPump ? new[] { PumpMinPct, 75, 100 } : new[] { 30, 60, 100 };
        fan.Testing = true;
        try
        {
            foreach (int step in steps)
            {
                int pct = SafeSpeed(step, fan.IsPump);
                lock (_hwLock) fan.CtrlSensor.Control.SetSoftware(pct);
                Thread.Sleep(2000);  // worker thread: give the fan time to settle
                float rpm = fan.RpmSensor.Value ?? 0f;
                readings.Add((pct, rpm));
                Console.WriteLine($"[health] {fanName} @ {pct}% → {rpm:F0} RPM");
            }
        }
        catch (Exception ex)
        {
            // The finally below still puts the fan back; the page gets an answer instead of silence
            Console.WriteLine($"[health] {fanName} check stopped: {ex.Message}");
            fan.HealthStatus = "FAIL";
            return new FanHealthResult { FanName = fanName, Status = "FAIL", Message = $"Check stopped partway ({ex.Message}). The fan was put back to its setting." };
        }
        finally
        {
            // Put the fan back the way it was: auto to the BIOS, manual to its speed,
            // a curve or profile picks up again on the next poll
            fan.LastSent = -1; fan.CurveSpeed = -1;
            try
            {
                if (fan.Mode == "auto") { lock (_hwLock) fan.CtrlSensor.Control.SetDefault(); }
                else if (fan.Mode == "manual") Send(fan, (int)fan.ManualPct);
            }
            catch (Exception ex) { Console.WriteLine($"[health] {fanName} restore failed: {ex.Message}"); }
            finally { fan.Testing = false; }   // the poll takes the fan over again either way
        }

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
            minPct      = f.IsPump ? PumpMinPct : 0,
            isPump      = f.IsPump,
            pumpByName  = f.PumpByName,
            failsafe    = f.Mode != "auto" && !f.Testing && (f.IsGpu ? _gpuGuard : _cpuGuard),
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
                    try { rpm = Convert.ToSingle(obj["DesiredSpeed"]); } catch { }   // no reading: show 0 rather than fail the whole list
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
            if (f.Mode == "auto" && !f.Testing) continue;
            try { lock (_hwLock) f.CtrlSensor?.Control?.SetDefault(); } catch (Exception ex) { Console.WriteLine($"[fans] {f.Name}: couldn't hand control back to the BIOS: {ex.Message}"); }
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
