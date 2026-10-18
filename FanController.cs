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
    public bool    failsafe   { get; set; }     // forced to 100% (over-temperature guard or no usable temperature)
    public string? failsafeWhy { get; set; }    // why, for the page
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
    // Shared between the poll (UI thread) and the health-check worker, hence volatile
    private volatile bool _testing, _abortTest;
    public bool     Testing      { get => _testing;   set => _testing = value; }     // health check running: the poll loop leaves it alone
    public bool     AbortTest    { get => _abortTest; set => _abortTest = value; }   // set by the poll when a guard trips during a health check
    public long     LastSentAt   { get; set; }   // when LastSent was written (the speed is re-sent every ResendMs)
    public bool     Guarded      { get; set; }   // forced to 100% by an over-temperature guard this tick
    public bool     NoTemp       { get; set; }   // on a curve/profile with no usable temperature → 100%
    public long     LastErrorAt  { get; set; }   // rate-limits the error log for this fan

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
    /// Called after LHM Computer is open and updated, and again by RescanIfNewFans
    /// when motherboard fans show up late.
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

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hw in allHw)
        {
            // Fan RPM sensors
            var fanSensors  = hw.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
            // Control sensors (0–100%)
            var ctrlSensors = hw.Sensors.Where(s => s.SensorType == SensorType.Control).ToList();
            var used = new HashSet<ISensor>();   // each control drives one fan only

            foreach (var fan in fanSensors)
            {
                // Try to match a control sensor by name similarity.
                // LHM names them in pairs like "Fan #2" (RPM) / "Fan Control #2" or "Fan #2" (%),
                // so drop "fan", "control", "#" and spaces and compare what's left.
                ISensor? ctrl = ctrlSensors.FirstOrDefault(c => !used.Contains(c) && Key(c.Name) == Key(fan.Name));

                // Fallback: match by index if names don't align. The Nth RPM sensor usually
                // belongs to the Nth control on the same chip, but this is a guess — the
                // health check on the Fan Control page is how a user can confirm the pairing.
                if (ctrl == null)
                {
                    int idx = fanSensors.IndexOf(fan);
                    if (idx < ctrlSensors.Count && !used.Contains(ctrlSensors[idx]))
                        ctrl = ctrlSensors[idx];
                }
                if (ctrl != null) used.Add(ctrl);

                // Names identify fans everywhere (saved curves, page commands), so they must be
                // unique. Two "GPU Fan"s on two cards, or "Fan #1" on two chips, get the chip's name added.
                string name = fan.Name;
                if (!names.Add(name)) { name = $"{fan.Name} ({hw.Name})"; for (int n = 2; !names.Add(name); n++) name = $"{fan.Name} ({hw.Name} {n})"; }

                found.Add(new FanEntry
                {
                    Name      = name,
                    RpmSensor = fan,
                    CtrlSensor= ctrl
                });

                Console.WriteLine($"[fans] {name} — control: {(ctrl != null ? ctrl.Name : "none")}");
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
    // GPU over-temperature guard: a GPU fan under manual or curve control is forced
    // to 100% when the core or hot spot gets this hot, and released 8 °C below.
    // Each graphics card has its own guard, fed by its own sensors.
    public const float GpuCoreLimit = 90f, GpuHotspotLimit = 100f, GpuGuardRelease = 8f;
    private sealed class GpuGuard { public bool On; public readonly TempTrack Core = new(), Hot = new(); }
    private readonly Dictionary<string, GpuGuard> _gpuGuards = new();

    // CPU over-temperature guard: case, CPU and pump fans under manual, curve or profile
    // control go to 100% at 95 °C (AMD's limit, a few degrees under Intel's) and are
    // released at 85 °C. Fans on auto are already the BIOS's job.
    public const float CpuLimit = 95f, CpuGuardRelease = 10f;
    private bool _cpuGuard;
    private readonly TempTrack _cpuTrack = new();

    // ─── Temperatures the fans can trust ─────────────────────────────────────
    // A reading only counts if it's a real number in a believable range and the
    // snapshot it came from is fresh. A sensor that worked and then stops (driver
    // unloaded, sensor poll stuck) is "lost" after LostAfterMs, and lost counts as
    // hot: when Vexis can't see the temperature, full speed is the safe answer.
    // StaleAfterMs is longer than SensorService.PausePoll's 5 s (while OpenRGB scans the RAM
    // bus), so starting OpenRGB doesn't send every fan to full speed.
    internal const long StaleAfterMs = 8000, LostAfterMs = 2000;

    internal static float? Usable(float? t) =>
        t is float v && float.IsFinite(v) && v > 0f && v < 150f ? v : null;

    /// <summary>Remembers whether a sensor has ever worked and how long it has been missing.</summary>
    internal sealed class TempTrack
    {
        public bool Seen;
        private long _badSince = -1;
        public GuardReading Read(float? temp, float limit, long now)
        {
            if (temp != null) { Seen = true; _badSince = -1; return new(temp, limit, true, false); }
            if (!Seen) return new(null, limit, false, false);          // this PC never had it
            if (_badSince < 0) _badSince = now;
            return new(null, limit, true, now - _badSince >= LostAfterMs);
        }
    }

    /// <param name="Seen">the sensor has worked before (so a missing value means it was lost)</param>
    /// <param name="Lost">missing for longer than LostAfterMs</param>
    internal readonly record struct GuardReading(float? Temp, float Limit, bool Seen = true, bool Lost = false);

    /// <summary>
    /// Guard state with hysteresis: switches on when any reading reaches its limit or a
    /// sensor is lost, and off only when every reading that exists is
    /// <paramref name="release"/> °C under its limit. While on, a reading that is
    /// briefly missing keeps it on.
    /// </summary>
    internal static bool NextGuard(bool on, float release, params GuardReading[] r) => on
        ? r.Any(x => x.Lost || (x.Temp is float t ? t >= x.Limit - release : x.Seen))
        : r.Any(x => x.Lost || x.Temp >= x.Limit);

    internal static bool NextGuard(bool on, float release, params (float temp, float limit)[] readings) =>
        NextGuard(on, release, readings.Select(x => new GuardReading(x.temp, x.limit)).ToArray());

    // A GPU fan follows the card it sits on; only if that card has no core sensor of
    // its own does it fall back to the main GPU's readings
    private static float? OwnGpuTemp(FanEntry fan, string sensorName)
    {
        foreach (var s in fan.RpmSensor.Hardware.Sensors)
            if (s.SensorType == SensorType.Temperature && s.Name == sensorName) return s.Value;
        return null;
    }
    private static bool HasOwnGpuCore(FanEntry fan) =>
        fan.RpmSensor.Hardware.Sensors.Any(s => s.SensorType == SensorType.Temperature && s.Name == "GPU Core");

    private bool GuardOn(FanEntry fan) =>
        fan.IsGpu ? _gpuGuards.TryGetValue(fan.RpmSensor.Hardware.Identifier.ToString(), out var g) && g.On : _cpuGuard;

    private bool _wasStale;

    private AppConfig? _config;

    /// <summary>
    /// Call every poll cycle. Applies the over-temperature guards, then each fan's
    /// manual speed, curve or profile.
    /// </summary>
    public void Update(SensorData sensorData, AppConfig config)
    {
        if (!_discovered) return;
        _config = config;
        RescanIfNewFans();

        // No sensor reading yet (the first moments after start): leave the fans as they are
        if (sensorData.taken_ms == 0) return;
        long now = Environment.TickCount64;
        bool stale = now - sensorData.taken_ms > StaleAfterMs;
        if (stale != _wasStale)
        {
            Console.WriteLine(stale ? "[fans] Sensor data is more than 8 s old — treating temperatures as missing."
                                    : "[fans] Sensor data is fresh again.");
            _wasStale = stale;
        }
        float? T(float? v) => stale ? null : Usable(v);

        // CPU guard
        bool wasCpuGuard = _cpuGuard;
        var cpuR = _cpuTrack.Read(T(sensorData.cpu_temp), CpuLimit, now);
        _cpuGuard = NextGuard(_cpuGuard, CpuGuardRelease, cpuR);
        if (_cpuGuard != wasCpuGuard)
            Console.WriteLine(_cpuGuard
                ? (cpuR.Lost ? "[fans] CPU temperature lost — case, CPU and pump fans at 100% until it's back."
                             : $"[fans] CPU over-temperature guard ON ({cpuR.Temp:F0}°C) — case, CPU and pump fans at 100%.")
                : "[fans] CPU over-temperature guard off — fans back to their setting.");

        // One guard per graphics card
        foreach (var hw in _fans.Where(f => f.IsGpu).Select(f => f.RpmSensor.Hardware).DistinctBy(h => h.Identifier.ToString()))
        {
            string key = hw.Identifier.ToString();
            if (!_gpuGuards.TryGetValue(key, out var g)) _gpuGuards[key] = g = new GpuGuard();
            var probe = _fans.First(f => f.IsGpu && f.RpmSensor.Hardware == hw);
            bool own = HasOwnGpuCore(probe);
            float? core = T(own ? OwnGpuTemp(probe, "GPU Core")     : sensorData.gpu?.temp_core ?? sensorData.gpu_temp);
            float? hot  = T(own ? OwnGpuTemp(probe, "GPU Hot Spot") : sensorData.gpu?.temp_hotspot);
            var cr = g.Core.Read(core, GpuCoreLimit, now);
            var hr = g.Hot.Read(hot, GpuHotspotLimit, now);
            bool was = g.On;
            g.On = NextGuard(g.On, GpuGuardRelease, cr, hr);
            if (g.On != was)
                Console.WriteLine(g.On
                    ? (cr.Lost || hr.Lost ? $"[fans] {hw.Name}: temperature lost — its fans at 100% until it's back."
                                          : $"[fans] {hw.Name}: over-temperature guard ON (core {cr.Temp:F0}°C, hot spot {hr.Temp:F0}°C) — its fans at 100%.")
                    : $"[fans] {hw.Name}: over-temperature guard off — fans back to their setting.");
        }

        string profile = FanProfiles.Normalize(config.FanProfile);
        foreach (var fan in _fans)
        {
            // One fan failing (sensor gone after a driver reset, ...) must not stop the
            // guards from reaching the fans after it
            try { UpdateFan(fan, sensorData, config, profile, stale); }
            catch (Exception ex)
            {
                if (now - fan.LastErrorAt > 60_000) { Console.WriteLine($"[fans] {fan.Name}: {ex.Message}"); fan.LastErrorAt = now; }
            }
        }
        // Tell the watchdog this pass completed
        Volatile.Write(ref _lastUpdateAt, Environment.TickCount64);
        if (_watchdogTripped) { _watchdogTripped = false; Console.WriteLine("[fans] Watchdog: fan updates running again — normal speeds restored."); }
    }

    private void UpdateFan(FanEntry fan, SensorData sensorData, AppConfig config, string profile, bool stale)
    {
        if (fan.CtrlSensor?.Control == null || fan.RpmSensor.Hardware.HardwareType == HardwareType.GpuIntel) return;
        bool hot = GuardOn(fan);

        // A health check is running: it owns the fan, unless its guard trips. Then the
        // check is stopped and the fan goes to full speed straight away.
        if (fan.Testing)
        {
            if (hot && !fan.AbortTest)
            {
                fan.AbortTest = true;
                lock (_hwLock) fan.CtrlSensor.Control.SetSoftware(100);
                Console.WriteLine($"[fans] {fan.Name}: too hot during the health check — stopped, fan at 100%.");
            }
            return;
        }
        if (fan.Mode == "auto") { fan.Guarded = fan.NoTemp = false; return; }   // the BIOS / driver's job

        bool wasGuarded = fan.Guarded;
        fan.Guarded = hot;
        if (hot) { Send(fan, 100); return; }
        if (wasGuarded && fan.Mode == "manual") Send(fan, (int)fan.ManualPct);

        FanCurve? curve = fan.Mode switch
        {
            "profile" => FanProfiles.CurveFor(profile, fan.Name, fan.IsGpu, fan.IsPump),
            "curve"   => config.GetCurve(fan.Name) is { Enabled: true } c && c.Points is { Count: > 0 } ? c : null,
            _         => null
        };
        if (curve == null) { fan.NoTemp = false; if (fan.Mode == "manual") Send(fan, (int)fan.ManualPct); return; }

        float? temp = SourceTemp(curve.TempSource, sensorData, fan, stale);
        if (temp == null)
        {
            if (!fan.NoTemp) Console.WriteLine($"[fans] {fan.Name}: no usable '{curve.TempSource ?? "cpu"}' temperature — running at 100% until it's back.");
            fan.NoTemp = true;
            Send(fan, 100);
            return;
        }
        if (fan.NoTemp) { fan.NoTemp = false; fan.CurveSpeed = -1; }
        Send(fan, CurveSpeed(fan, curve, temp.Value));
    }

    // The temperature a curve follows, or null if there's no usable reading
    // (the caller then runs the fan at 100%)
    private static float? SourceTemp(string? source, SensorData d, FanEntry fan, bool stale)
    {
        if (stale) return null;
        bool own = fan.IsGpu && HasOwnGpuCore(fan);
        float? v = source switch
        {
            "ccd0"        => Usable(d.ccd0_temp) ?? d.cpu_temp,
            "ccd1"        => Usable(d.ccd1_temp) ?? d.cpu_temp,
            "gpu"         => own ? OwnGpuTemp(fan, "GPU Core")     : d.gpu?.temp_core ?? d.gpu_temp,
            "gpu_hotspot" => own ? OwnGpuTemp(fan, "GPU Hot Spot") ?? OwnGpuTemp(fan, "GPU Core") : d.gpu?.temp_hotspot ?? d.gpu_temp,
            "gpu_mem"     => d.gpu?.temp_mem ?? d.gpu_temp,
            _             => d.cpu_temp
        };
        return Usable(v);
    }

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

    // Every speed Vexis sets goes through here: made safe, and sent when it changes.
    // It's also re-sent every ResendMs, in case the board took control back (sleep /
    // resume, or another fan program) without Vexis knowing.
    internal const long ResendMs = 10_000;
    private static void Send(FanEntry fan, int pct)
    {
        pct = SafeSpeed(pct, fan.IsPump);
        long now = Environment.TickCount64;
        if (pct == fan.LastSent && now - fan.LastSentAt < ResendMs) return;
        lock (_hwLock) fan.CtrlSensor?.Control?.SetSoftware(pct);
        fan.LastSent = pct; fan.LastSentAt = now;
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
            try
            {
                if (fan.Mode == "manual") { Send(fan, (int)fan.ManualPct); continue; }
                if (config.GetCurve(fan.Name) is { Enabled: true } c && c.Points is { Count: > 0 }) fan.Mode = "curve";
                else { fan.Mode = "auto"; lock (_hwLock) fan.CtrlSensor.Control.SetDefault(); }
            }
            catch (Exception ex) { Console.WriteLine($"[fans] {fan.Name}: couldn't apply its setting: {ex.Message}"); }
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
            var previous = _fans.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First());
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
                    else if (_config.GetCurve(f.Name) is { Enabled: true } c && c.Points is { Count: > 0 }) f.Mode = "curve";
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
    /// Runs a brief health check: steps the fan through 30%, 60%, 100% (pumps 50%, 75%, 100%),
    /// reads the RPM at each step, then puts the fan back in the mode it was in.
    /// Returns results after ~8 seconds. Call on a background thread.
    /// </summary>
    public FanHealthResult RunHealthCheck(string fanName)
    {
        var fan = Find(fanName);
        if (fan == null)
            return new FanHealthResult { FanName = fanName, Status = "NOT_FOUND", Message = "Fan not found." };

        if (fan.CtrlSensor?.Control == null)
            return new FanHealthResult { FanName = fanName, Status = "NO_CONTROL", Message = "Fan has no software control — BIOS controlled only." };

        // Not while its over-temperature guard has the fan at full speed
        if (GuardOn(fan))
            return new FanHealthResult { FanName = fanName, Status = "SKIPPED", Message = "Too hot to test right now: the fan is at full speed until it cools down." };

        var readings = new List<(int SetPct, float MeasuredRpm)>();

        // Three steps, low to full. Pumps start at their floor, never lower. The poll
        // loop skips the fan while Testing is set, so the two don't fight over it.
        int[] steps = fan.IsPump ? new[] { PumpMinPct, 75, 100 } : new[] { 30, 60, 100 };
        // If the guard trips during the check, the poll sets AbortTest and puts the fan at
        // 100%; the check then stops before writing another step.
        fan.AbortTest = false;
        fan.Testing = true;
        try
        {
            foreach (int step in steps)
            {
                int pct = SafeSpeed(step, fan.IsPump);
                lock (_hwLock)
                {
                    if (fan.AbortTest) throw new TooHotException();
                    fan.CtrlSensor.Control.SetSoftware(pct);
                }
                // worker thread: give the fan 2 s to settle, checking for the guard as it goes
                for (int w = 0; w < 8; w++) { Thread.Sleep(250); if (fan.AbortTest) throw new TooHotException(); }
                float rpm = fan.RpmSensor.Value ?? 0f;
                readings.Add((pct, rpm));
                Console.WriteLine($"[health] {fanName} @ {pct}% → {rpm:F0} RPM");
            }
        }
        catch (TooHotException)
        {
            fan.HealthStatus = null;
            return new FanHealthResult { FanName = fanName, Status = "STOPPED", Message = "Stopped: it got too hot during the check, so the fan went to full speed." };
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
                if (fan.AbortTest && fan.Mode != "auto") Send(fan, 100);       // the guard takes it from here
                else if (fan.Mode == "auto") { lock (_hwLock) fan.CtrlSensor.Control.SetDefault(); }
                else if (fan.Mode == "manual") Send(fan, (int)fan.ManualPct);
            }
            catch (Exception ex) { Console.WriteLine($"[health] {fanName} restore failed: {ex.Message}"); }
            finally { fan.Testing = false; }   // the poll takes the fan over again either way
        }

        // Evaluate (readings are in step order: lowest speed first, 100% last)
        //   responding: every step spins above 100 RPM
        //   scaling:    100% is at least 20% faster than the lowest step, so the control works
        //   stalling:   under 50 RPM at a speed that should spin any fan
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
            // Intel Arc lists a control sensor, but LHM can't set its fan speed
            hasControl  = f.CtrlSensor?.Control != null &&
                          f.RpmSensor.Hardware.HardwareType != HardwareType.GpuIntel,
            isGpu       = f.IsGpu,
            minPct      = f.IsPump ? PumpMinPct : 0,
            isPump      = f.IsPump,
            pumpByName  = f.PumpByName,
            failsafe    = !f.Testing && (f.Guarded || f.NoTemp),
            failsafeWhy = f.NoTemp ? "No temperature reading — running at 100% until it's back"
                        : f.Guarded ? (f.IsGpu ? "GPU too hot — forced to 100% until it cools down" : "CPU too hot — forced to 100% until it cools down")
                        : null,
            curveActive = f.Mode == "curve",
            mode        = f.Mode,
            healthStatus= f.HealthStatus
        }).ToList();

        // Append WMI fallback fans if LHM found none from the motherboard.
        // Win32_Fan has no live RPM; DesiredSpeed is the speed the board asks for, and many
        // boards leave Win32_Fan empty, so this is a best effort.
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
        StopWatchdog();   // fans are being handed back on purpose
        foreach (var f in _fans)
        {
            if (f.Mode == "auto" && !f.Testing) continue;
            try { lock (_hwLock) f.CtrlSensor?.Control?.SetDefault(); } catch (Exception ex) { Console.WriteLine($"[fans] {f.Name}: couldn't hand control back to the BIOS: {ex.Message}"); }
            f.Mode = "auto";
        }
        Console.WriteLine("[fans] Fan control handed back to BIOS / driver.");
    }

    // ─── Watchdog ──────────────────────────────────────────────────────────────
    // Update() runs on the UI thread (MainForm's poll timer). If that thread hangs — a stuck
    // WebView2 call, a modal dialog, a deadlock — no fan code runs at all, guards included.
    // This timer runs on its own thread: if Update hasn't completed for WatchdogMs, every fan
    // Vexis controls goes to 100% until Update runs again (which re-sends the normal speeds).
    internal const long WatchdogMs = 10_000;
    private System.Threading.Timer? _watchdog;
    private long _lastUpdateAt;      // Environment.TickCount64 of the last full Update
    private volatile bool _watchdogTripped;

    /// <summary>Starts the watchdog. Call once fans are discovered and the profile is applied.</summary>
    public void StartWatchdog()
    {
        Volatile.Write(ref _lastUpdateAt, Environment.TickCount64);   // the first Update gets the full 10 s
        _watchdog ??= new System.Threading.Timer(_ => WatchdogTick(), null, 1000, 1000);
    }

    private void StopWatchdog() { _watchdog?.Dispose(); _watchdog = null; }

    /// <summary>True when fans should be forced to full speed: no completed Update for too long.</summary>
    internal static bool WatchdogShouldTrip(long lastUpdateAt, long now) => now - lastUpdateAt > WatchdogMs;

    private void WatchdogTick()
    {
        try
        {
            if (_watchdogTripped || !WatchdogShouldTrip(Volatile.Read(ref _lastUpdateAt), Environment.TickCount64)) return;
            _watchdogTripped = true;
            Console.WriteLine("[fans] WATCHDOG: fan updates stopped for 10 s — every fan Vexis controls set to 100%.");
            foreach (var f in _fans.ToList())   // a copy: rediscovery may swap the list
            {
                if (f.Mode == "auto" && !f.Testing) continue;   // the BIOS / driver has it
                if (f.CtrlSensor?.Control == null) continue;
                try { lock (_hwLock) f.CtrlSensor.Control.SetSoftware(100); }
                catch (Exception ex) { Console.WriteLine($"[fans] WATCHDOG: {f.Name}: {ex.Message}"); }
                f.LastSent = -1;   // the next Update re-sends the real speed
            }
        }
        catch (Exception ex) { Console.WriteLine($"[fans] WATCHDOG error: {ex.Message}"); }
    }

    // Tests: run the controller on fake fans (no hardware)
    internal void UseFansForTest(IEnumerable<FanEntry> fans) { _fans = fans.ToList(); _discovered = true; }

    public bool IsDiscovered => _discovered;
    public List<string> FanNames => _fans.Select(f => f.Name).ToList();

    // "Fan Control #2" and "Fan #2" → "2"; "GPU Fan" → "gpu"
    internal static string Key(string sensorName) =>
        new string(sensorName.ToLowerInvariant().Replace("fan", "").Replace("control", "").Where(ch => ch != '#' && !char.IsWhiteSpace(ch)).ToArray());

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

internal sealed class TooHotException : Exception { }
