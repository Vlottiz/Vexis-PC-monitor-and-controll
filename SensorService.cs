using Timer = System.Threading.Timer;
using LibreHardwareMonitor.Hardware;
using System.Management;
using System.Net.Http;
using System.Text.RegularExpressions;

public class HwInfo
{
    public string cpu     { get; set; } = "";
    public string vendor  { get; set; } = "";  // "amd" | "intel" | "other"
    public int    cores   { get; set; }
    public int    threads { get; set; }
    public string socket  { get; set; } = "";
    public string gpu     { get; set; } = "";
    public int    gpuVram { get; set; }
    public int    ramGb   { get; set; }
    public int    ramMhz  { get; set; }
    public string ramType { get; set; } = "";
    public int    ccdCount  { get; set; } = 0;   // AMD CCDs, 0 for Intel/other
    public bool   x3d       { get; set; } = false; // AMD 3D V-Cache part
    public int    baseMhz   { get; set; } = 0;     // rated base clock (scales the core bars)
    public bool   driverOk  { get; set; } = false; // true = LHM ring-0 driver reading OK
}

public class SensorData
{
    public float?  cpu_temp    { get; set; }
    public float?  ccd0_temp   { get; set; }
    public float?  ccd1_temp   { get; set; }
    public float?  pkg_power   { get; set; }
    public float?  gpu_temp    { get; set; }
    public float?  gpu_voltage { get; set; }
    public float?  gpu_power   { get; set; }
    public float?  gpu_clock   { get; set; }
    public float?  avg_clk     { get; set; }
    public float?  cpu_load    { get; set; }
    public float?  gpu_load    { get; set; }
    public float?  ram_load    { get; set; }
    public float?  ram_used_gb { get; set; }
    public float?  virt_used_gb { get; set; }  // commit charge (RAM + page file)
    public float?  virt_load    { get; set; }
    public List<float>? dimm_temps { get; set; }
    public Dictionary<int, CoreInfo>  cores     { get; set; } = new();
    public Dictionary<string, float>  all_temps { get; set; } = new();
    public List<FanInfo>?             fans      { get; set; }
    public HwInfo?                    info      { get; set; }
}

public class CoreInfo
{
    public float  clk  { get; set; }
    public int    rank { get; set; }  // 0 = no rank info (Intel/generic)
    public float? temp { get; set; }  // per-core temp (Intel; AMD Zen only exposes CCD temps)
    public float? load { get; set; }  // per-core load % (busiest thread of the core)
    public string? kind { get; set; } // "P" / "E" on Intel hybrid CPUs, null otherwise
}

public class SensorService : IDisposable
{
    private readonly Computer   _computer;
    private readonly Timer      _timer;
    private          HwInfo     _hwInfo   = new();
    private volatile SensorData _latest   = new();
    private          bool       _disposed;
    private          bool       _firstPoll = true;

    // Dynamic core map — built per-CPU in ScanHardware
    private Dictionary<int, (int displayId, int rank)> _coreMap = new();

    public event Action<SensorData>? OnUpdate;
    public int PollIntervalMs { get; set; } = 250;

    public IEnumerable<IHardware> AllHardware => _computer.Hardware;
    public SensorData GetLatestData() => _latest;

    // Matches: "Core #1", "CPU Core #1", "P-Core #1", "E-Core #1" (Intel 12th gen+)
    private static readonly Regex CoreClockRe = new(
        @"^(?:CPU |P-|E-)?Core #?(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Per-thread suffix on load sensors, e.g. "CPU Core #3 Thread #2"
    private static readonly Regex ThreadSuffixRe = new(
        @" Thread #\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private AppConfig? _config;

    public SensorService(AppConfig? config = null)
    {
        _config = config;
        _computer = new Computer
        {
            IsCpuEnabled         = true,
            IsGpuEnabled         = true,
            IsMemoryEnabled      = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled  = false,
            IsNetworkEnabled     = false,
            IsStorageEnabled     = false
        };
        // ── Sensor driver ────────────────────────────────────────────────────────
        // LHM 0.9.6 reads per-core temps/clocks, SMU, SuperIO fans and DIMM temps
        // through PawnIO. It works with Memory Integrity / VBS / the driver blocklist
        // left ON, so Vexis no longer changes any Windows security settings.
        DriverSetup.CleanupLegacyWinRing0();
        DriverSetup.EnsurePawnIo();

        try
        {
            _computer.Open();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[hw] LHM Open() failed: {ex.Message}");
        }

        ScanHardware();
        _timer = new Timer(Poll, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(0, PollIntervalMs);
    public void Stop()  => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    private void ScanHardware()
    {
        Console.WriteLine("[hw] Scanning hardware...");
        string bestGpuName = "";
        int    bestGpuVram = 0;

        foreach (var hw in _computer.Hardware)
        {
            hw.Update(); hw.Update();
            // Also update and log all sub-hardware (SuperIO, EC chips)
            foreach (var sub in hw.SubHardware)
            {
                sub.Update();
                // SuperIO reads are skipped while another app holds the ISA bus lock —
                // give it a few more tries before reporting what we found.
                for (int retry = 0; retry < 5 && sub.HardwareType == HardwareType.SuperIO &&
                     !sub.Sensors.Any(x => x.SensorType is SensorType.Fan or SensorType.Temperature); retry++)
                {
                    Thread.Sleep(200);
                    sub.Update();
                }
                if (sub.HardwareType == HardwareType.SuperIO &&
                    !sub.Sensors.Any(x => x.SensorType is SensorType.Fan or SensorType.Temperature))
                    Console.WriteLine($"[hw]   ! {sub.Name} could not be read — another program (MSI Center, " +
                                      "HWiNFO, AIDA64, ...) is probably holding the hardware bus. Close it and restart Vexis.");
                int fanCount  = sub.Sensors.Count(s => s.SensorType == SensorType.Fan);
                int ctrlCount = sub.Sensors.Count(s => s.SensorType == SensorType.Control);
                Console.WriteLine($"[hw]   SubHW: [{sub.HardwareType}] {sub.Name} — {fanCount} fan, {ctrlCount} ctrl sensors");
                if (fanCount > 0)
                    foreach (var s in sub.Sensors.Where(s => s.SensorType == SensorType.Fan))
                        Console.WriteLine($"[hw]     Fan: {s.Name} = {s.Value?.ToString("F0") ?? "null"} RPM");
            }
            Console.WriteLine($"[hw] Found: [{hw.HardwareType}] {hw.Name}");

            if (hw.HardwareType == HardwareType.Cpu)
            {
                string name = hw.Name;
                _hwInfo.cpu = CleanCpuName(name);

                // Detect vendor
                if (name.Contains("AMD") || name.Contains("Ryzen") || name.Contains("EPYC"))
                    _hwInfo.vendor = "amd";
                else if (name.Contains("Intel") || name.Contains("Core") || name.Contains("Xeon"))
                    _hwInfo.vendor = "intel";
                else
                    _hwInfo.vendor = "other";

                // Build dynamic core map from actual clock sensors
                BuildCoreMap(hw);

                // Thread count — use WMI NumberOfLogicalProcessors (most accurate)
                // Intel hybrid: P-cores have HT (2 threads each), E-cores don't
                // So 8P+8E = 8×2 + 8×1 = 24 threads — WMI gets this right, sensor counting doesn't
                try
                {
                    using var wq = new ManagementObjectSearcher(
                        "SELECT NumberOfLogicalProcessors FROM Win32_Processor");
                    foreach (ManagementObject obj in wq.Get())
                    {
                        _hwInfo.threads = Convert.ToInt32(obj["NumberOfLogicalProcessors"]);
                        break;
                    }
                }
                catch { }
                if (_hwInfo.threads == 0) _hwInfo.threads = _hwInfo.cores;

                // CCD count for AMD
                if (_hwInfo.vendor == "amd")
                {
                    // One "CCDn (Tdie)" sensor per CCD — don't count "CCDs Max/Average"
                    _hwInfo.ccdCount = hw.Sensors
                        .Where(s => s.SensorType == SensorType.Temperature &&
                                    Regex.IsMatch(s.Name, @"^CCD\d+", RegexOptions.IgnoreCase))
                        .Select(s => s.Name).Distinct().Count();
                    _hwInfo.x3d = name.Contains("X3D", StringComparison.OrdinalIgnoreCase);
                }

                // Check if driver loaded OK (any temp sensor has a real value)
                _hwInfo.driverOk = hw.Sensors.Any(s =>
                    s.SensorType == SensorType.Temperature &&
                    s.Value.HasValue && s.Value.Value > 1f);

                Console.WriteLine($"[hw]   → {_hwInfo.vendor.ToUpper()} CPU: {_hwInfo.cpu}, {_hwInfo.cores}C/{_hwInfo.threads}T, driver={_hwInfo.driverOk}");

                // Dump temp/power sensors
                Console.WriteLine("[hw]   → Temperature sensors:");
                foreach (var s in hw.Sensors.Where(x => x.SensorType == SensorType.Temperature))
                    Console.WriteLine($"[hw]      '{s.Name}' = {s.Value?.ToString("F2") ?? "null"}");
                Console.WriteLine("[hw]   → Power sensors:");
                foreach (var s in hw.Sensors.Where(x => x.SensorType == SensorType.Power).Take(3))
                    Console.WriteLine($"[hw]      '{s.Name}' = {s.Value?.ToString("F2") ?? "null"}");
            }

            bool isGpu = hw.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel;
            if (isGpu)
            {
                var vramSensor = hw.Sensors.FirstOrDefault(s => s.Name == "GPU Memory Total");
                int vramMb = vramSensor?.Value.HasValue == true ? (int)vramSensor.Value.Value : 0;
                Console.WriteLine($"[hw]   → GPU: {hw.Name}, VRAM: {vramMb} MB");
                if (vramMb > bestGpuVram) { bestGpuVram = vramMb; bestGpuName = hw.Name; }
            }
        }

        if (!string.IsNullOrEmpty(bestGpuName))
        {
            _hwInfo.gpu     = CleanGpuName(bestGpuName);
            _hwInfo.gpuVram = (int)Math.Round(bestGpuVram / 1024f);
        }

        try
        {
            using var q = new ManagementObjectSearcher("SELECT Speed,ConfiguredClockSpeed,SMBIOSMemoryType,Capacity FROM Win32_PhysicalMemory");
            long total = 0;
            foreach (ManagementObject obj in q.Get())
            {
                total += Convert.ToInt64(obj["Capacity"]);
                if (_hwInfo.ramMhz == 0) _hwInfo.ramMhz = RamSpeedMts(obj);
                if (string.IsNullOrEmpty(_hwInfo.ramType))
                    _hwInfo.ramType = Convert.ToInt32(obj["SMBIOSMemoryType"]) switch
                    { 34=>"DDR5", 26=>"DDR4", 24=>"DDR3", _=>"DDR" };
            }
            _hwInfo.ramGb = (int)(total / (1024L * 1024 * 1024));
        }
        catch { }

        try
        {
            using var q = new ManagementObjectSearcher("SELECT SocketDesignation, MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject obj in q.Get())
            {
                _hwInfo.socket  = obj["SocketDesignation"]?.ToString() ?? "";
                _hwInfo.baseMhz = Convert.ToInt32(obj["MaxClockSpeed"] ?? 0);
                break;
            }
            _hwInfo.socket = NormalizeSocket(_hwInfo.socket, _hwInfo.cpu, _hwInfo.vendor);
        }
        catch { }

        Console.WriteLine($"[hw] CPU: {_hwInfo.cpu} ({_hwInfo.cores}C/{_hwInfo.threads}T, {_hwInfo.socket}, vendor={_hwInfo.vendor})");
        Console.WriteLine($"[hw] GPU: {_hwInfo.gpu} ({_hwInfo.gpuVram}GB)");
        Console.WriteLine($"[hw] RAM: {_hwInfo.ramGb}GB {_hwInfo.ramType}-{_hwInfo.ramMhz}");
    }

    // RAM speed in MT/s (what Task Manager shows as "Speed"). ConfiguredClockSpeed is
    // the running speed (XMP/EXPO applied); Speed is the module rating. Some boards
    // report DDR5 values doubled (12800 for DDR5-6400) — no DDR4/DDR5 kit runs above
    // ~10000 MT/s, so halve anything beyond that.
    private static int RamSpeedMts(ManagementObject obj)
    {
        int Read(string prop) { try { return Convert.ToInt32(obj[prop] ?? 0); } catch { return 0; } }
        int mts = Read("ConfiguredClockSpeed");
        if (mts <= 0) mts = Read("Speed");
        while (mts > 10000) mts /= 2;
        return mts;
    }

    // SocketDesignation is whatever the board vendor typed into SMBIOS — often a real
    // socket ("AM5", "LGA1700") but sometimes a slot label like "U3E1" or "CPU 1".
    // Keep it if it looks like a socket, otherwise infer it from the CPU model.
    private static readonly Regex SocketLikeRe = new(
        @"^(FC)?LGA\s?\d{3,4}|^(FC)?BGA|^FP\d|^AM[2-5]\+?$|^s?TR[X]?\d|^SP\d|^FM\d|^Socket\s+(AM|LGA|FM|TR|\d{3,4})", RegexOptions.IgnoreCase);

    private static string NormalizeSocket(string raw, string cpu, string vendor)
    {
        raw = raw.Trim();
        if (SocketLikeRe.IsMatch(raw))
        {
            raw = Regex.Replace(raw, @"^Socket\s+", "", RegexOptions.IgnoreCase);
            return raw.StartsWith("FCLGA", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
        }

        if (vendor == "intel")
        {
            if (Regex.IsMatch(cpu, @"Ultra\s*\d\s*2\d\d[A-Z]*$", RegexOptions.IgnoreCase)) return "LGA1851";
            var m = Regex.Match(cpu, @"i\d-(\d{4,5})");
            if (m.Success)
            {
                string n = m.Groups[1].Value;
                int gen = n.Length == 5 ? int.Parse(n[..2]) : int.Parse(n[..1]);
                return gen switch
                {
                    >= 12 and <= 14 => "LGA1700",
                    10 or 11        => "LGA1200",
                    8 or 9          => "LGA1151",
                    _               => ""
                };
            }
        }
        else if (vendor == "amd")
        {
            if (cpu.Contains("Threadripper", StringComparison.OrdinalIgnoreCase)) return "";
            var m = Regex.Match(cpu, @"Ryzen\s+\d\s+(\d)\d{3}");
            if (m.Success) return m.Groups[1].Value[0] >= '7' ? "AM5" : "AM4";
        }
        return ""; // unknown — show nothing rather than a board slot label
    }

    // ── Build dynamic core map for any CPU ─────────────────────────────────────
    // Separate regex for Intel hybrid P-Core/E-Core (12th gen+)
    private static readonly Regex PCoreClockRe = new(@"^P-Core #?(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ECoreClockRe = new(@"^E-Core #?(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private bool _hybrid; // Intel P-core + E-core CPU

    private void BuildCoreMap(IHardware hw)
    {
        var allClockSensors = hw.Sensors
            .Where(s => s.SensorType == SensorType.Clock)
            .ToList();

        Console.WriteLine($"[hw]   → All clock sensors: {string.Join(", ", allClockSensors.Select(s => s.Name))}");

        _coreMap = new();

        // Intel 12th gen+: P-Core + E-Core need separate index spaces
        var pCores = allClockSensors
            .Where(s => PCoreClockRe.IsMatch(s.Name))
            .Select(s => int.Parse(PCoreClockRe.Match(s.Name).Groups[1].Value))
            .OrderBy(i => i).ToList();
        var eCores = allClockSensors
            .Where(s => ECoreClockRe.IsMatch(s.Name))
            .Select(s => int.Parse(ECoreClockRe.Match(s.Name).Groups[1].Value))
            .OrderBy(i => i).ToList();

        if (pCores.Count > 0 || eCores.Count > 0)
        {
            int displayId = 0;
            foreach (int idx in pCores)
                _coreMap[idx] = (displayId++, 0);
            foreach (int idx in eCores)
                _coreMap[1000 + idx] = (displayId++, 0);  // offset prevents collision
            _hwInfo.cores = pCores.Count + eCores.Count;
            _hybrid = true;
            Console.WriteLine($"[hw]   --> Intel hybrid: {pCores.Count} P-Cores + {eCores.Count} E-Cores = {_hwInfo.cores} total");
            return;
        }

        // Generic / AMD
        var genericIndices = allClockSensors
            .Where(s => CoreClockRe.IsMatch(s.Name))
            .Select(s => int.Parse(CoreClockRe.Match(s.Name).Groups[1].Value))
            .OrderBy(i => i).ToList();

        _hwInfo.cores = genericIndices.Count;

        int did = 0;
        foreach (int idx in genericIndices)
            _coreMap[idx] = (did++, 0);
        Console.WriteLine($"[hw]   --> Built sequential core map: {_coreMap.Count} cores");
    }


    private int _polling; // 1 while a poll is running — LHM Update() is not re-entrant

    private void Poll(object? _)
    {
        if (_paused) return; // LHM SMBus released for OpenRGB
        if (Interlocked.Exchange(ref _polling, 1) == 1) return; // previous poll still running
        try { PollCore(); }
        catch (Exception ex) { Console.WriteLine($"[poll] {ex.Message}"); }
        finally { Volatile.Write(ref _polling, 0); }
    }

    private void PollCore()
    {
        var data = new SensorData { info = _hwInfo };

        foreach (var hw in _computer.Hardware)
        {
            hw.Update();
            if (hw.HardwareType == HardwareType.Cpu) hw.Update();

            // Update sub-hardware (SuperIO chip — contains motherboard fan sensors)
            foreach (var sub in hw.SubHardware) sub.Update();

            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:    ReadCpu(hw, data); ReadCpuLoad(hw, data); break;
                case HardwareType.GpuAmd:
                case HardwareType.GpuNvidia:
                case HardwareType.GpuIntel:
                    if (CleanGpuName(hw.Name) == _hwInfo.gpu) ReadGpu(hw, data);
                    break;
            }

            // RAM load, used GB, and DIMM temps from Memory hardware
            // "Virtual Memory" and "Total Memory" use the same sensor names in LHM,
            // so tell them apart by hardware name (Virtual Memory is listed first).
            if (hw.HardwareType == HardwareType.Memory)
            {
                hw.Update();
                bool isVirtual = hw.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType == SensorType.Load && s.Name == "Memory" && s.Value.HasValue)
                    {
                        if (isVirtual) data.virt_load ??= s.Value.Value;
                        else           data.ram_load  ??= s.Value.Value;
                    }
                    if (s.SensorType == SensorType.Data && s.Name == "Memory Used" && s.Value.HasValue)
                    {
                        if (isVirtual) data.virt_used_gb ??= s.Value.Value;
                        else           data.ram_used_gb  ??= s.Value.Value;
                    }
                    if (IsRealTemp(s))
                    {
                        data.dimm_temps ??= new List<float>();
                        data.dimm_temps.Add(s.Value!.Value);
                    }
                }
            }

            string prefix = hw.HardwareType switch
            {
                HardwareType.Cpu         => "CPU",
                HardwareType.GpuAmd      => "GPU",
                HardwareType.GpuNvidia   => "GPU",
                HardwareType.GpuIntel    => "GPU",
                HardwareType.Motherboard => "MB",
                _                        => hw.HardwareType.ToString()
            };
            // Motherboard temps live on its SuperIO sub-hardware
            foreach (var s in hw.Sensors.Concat(hw.SubHardware.SelectMany(sub => sub.Sensors)))
                if (IsRealTemp(s))
                    data.all_temps[$"{prefix}/{s.Name}"] = (float)Math.Round(s.Value!.Value, 2);
        }

        // ── WMI fallback when LHM driver can't read (blocked by BIOS/firmware) ───
        if (data.cores.Count == 0 || data.cpu_temp == null)
            ApplyWmiFallback(data);

        // ── Re-check driverOk each poll — driver may load late ───────────────────
        // If PawnIO registered after startup (manual registration path) LHM may start
        // returning real values mid-session. Clear the "blocked" warning automatically.
        if (!_hwInfo.driverOk && data.cpu_temp.HasValue && data.cpu_temp.Value > 1f)
        {
            _hwInfo.driverOk = true;
            Console.WriteLine("[hw] Driver now OK (late-read confirmed)");
        }

        if (_firstPoll)
        {
            _firstPoll = false;
            Console.WriteLine($"[poll] First: cpu={data.cpu_temp?.ToString("F1")??"null"} pkg={data.pkg_power?.ToString("F1")??"null"} cores={data.cores.Count} (wmi={data.info?.driverOk==false})");
        }

        if (data.cores.Count > 0)
            data.avg_clk = data.cores.Values
                .Where(c => c.clk > 0).Select(c => c.clk)
                .DefaultIfEmpty(0).Average();

        _latest = data;
        OnUpdate?.Invoke(data);
    }

    // A live temperature reading — not a threshold ("Thermal Sensor High Limit",
    // "... Critical High Limit") and not an empty slot / unconnected probe.
    private static bool IsRealTemp(ISensor s) =>
        s.SensorType == SensorType.Temperature &&
        s.Value is > 1f and < 150f &&
        !s.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase) &&
        !s.Name.Contains("Threshold", StringComparison.OrdinalIgnoreCase);

    // ── CPU load from LHM sensors (AMD gets this for free; Intel needs WMI fallback) ──
    private void ReadCpuLoad(IHardware hw, SensorData data)
    {
        if (data.cpu_load != null) return; // already set
        foreach (var s in hw.Sensors)
            if (s.SensorType == SensorType.Load &&
                s.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase) &&
                s.Value.HasValue)
            { data.cpu_load = s.Value.Value; return; }
        // Fallback: average of all core load sensors
        var loads = hw.Sensors
            .Where(s => s.SensorType == SensorType.Load && s.Value.HasValue)
            .Select(s => s.Value!.Value).ToList();
        if (loads.Count > 0) data.cpu_load ??= loads.Average();
    }

    // ── Fallback for when LHM driver is blocked ───────────────────────────────
    // AMD:   LHM gives real per-core clocks via SMU — this method never runs
    // Intel: LHM MSR blocked → use Windows Performance Counters for live clock speed
    //        Formula: ActualMHz = MaxMHz × (% Processor Performance ÷ 100)
    //        Much more accurate than WMI CurrentClockSpeed (base clock only)
    private float _maxMhz        = 0;
    private bool  _perfInitialized = false;
    private System.Diagnostics.PerformanceCounter? _perfCounter = null;

    private void ApplyWmiFallback(SensorData data)
    {
        // Only run if LHM gave us nothing useful
        bool needClocks = data.cores.Count == 0;
        bool needTemp   = data.cpu_temp == null;
        if (!needClocks && !needTemp) return;

        try
        {
            // Read CPU load from WMI every tick (cheap query)
            try {
                using var lq = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor");
                foreach (ManagementObject obj in lq.Get()) {
                    data.cpu_load = Convert.ToSingle(obj["LoadPercentage"]);
                    break;
                }
            } catch { }

            // One-time: get actual max turbo frequency
            if (_maxMhz == 0)
            {
                // Registry has the real max turbo, WMI only has base clock
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"HARDWARE\DESCRIPTION\System\CentralProcessor");
                    if (key != null)
                    {
                        // ~MHz is base, MaxClockSpeed from WMI is also base
                        // But processor name string contains max turbo e.g. "3.20GHz"
                        // Better: use CurrentClockSpeed × performance% at 100% load
                        // For now, read ~MHz and multiply by known turbo ratio from WMI
                        var mhzVal = key.GetValue("~MHz");
                        if (mhzVal != null) _maxMhz = Convert.ToSingle(mhzVal);
                    }
                }
                catch { }

                // WMI fallback for base clock
                if (_maxMhz == 0)
                {
                    using var q = new ManagementObjectSearcher(
                        "SELECT MaxClockSpeed FROM Win32_Processor");
                    foreach (ManagementObject obj in q.Get())
                    { _maxMhz = Convert.ToSingle(obj["MaxClockSpeed"]); break; }
                }

                // Scale up: Windows % Processor Performance can exceed 100% during turbo
                // So we use base clock as the 100% reference — turbo shows as >100%
                // which gives us correct boosted MHz naturally
                Console.WriteLine($"[perf] Base clock reference: {_maxMhz}MHz");
            }

            // One-time: create performance counter for % Processor Performance
            // This reflects actual frequency scaling including Turbo Boost
            if (!_perfInitialized && _maxMhz > 0)
            {
                try
                {
                    _perfInitialized = true; // set before creation to prevent retry spam
                    _perfCounter = new System.Diagnostics.PerformanceCounter(
                        "Processor Information", "% Processor Performance", "_Total");
                    _perfCounter.NextValue(); // first call always returns 0 — discard
                    Console.WriteLine("[perf] Performance counter initialized.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[perf] Counter init failed: {ex.Message}");
                }
            }

            // Get live clock: MaxMHz × (% Processor Performance / 100)
            float actualMhz = _maxMhz; // fallback to max if counter fails
            if (_perfCounter != null && _maxMhz > 0)
            {
                try
                {
                    float perfPct = _perfCounter.NextValue();
                    if (perfPct > 0)
                        actualMhz = _maxMhz * (perfPct / 100f);
                }
                catch { }
            }

            // Populate core slots with live frequency
            if (needClocks && actualMhz > 0)
            {
                int coreCount = _hwInfo.cores > 0 ? _hwInfo.cores : 16;
                for (int i = 0; i < coreCount; i++)
                    if (!data.cores.ContainsKey(i))
                        data.cores[i] = new CoreInfo { clk = actualMhz, rank = 0 };
                data.avg_clk ??= actualMhz;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[perf] Fallback error: {ex.Message}");
        }
    }


    // Maps an LHM core sensor name to the key used in _coreMap (-1 = not a core sensor)
    private static int CoreKey(string name)
    {
        var pm = PCoreClockRe.Match(name);
        if (pm.Success && int.TryParse(pm.Groups[1].Value, out int pi)) return pi;
        var em = ECoreClockRe.Match(name);
        if (em.Success && int.TryParse(em.Groups[1].Value, out int ei)) return 1000 + ei;
        var gm = CoreClockRe.Match(name);
        if (gm.Success && int.TryParse(gm.Groups[1].Value, out int gi)) return gi;
        return -1;
    }

    private void ReadCpu(IHardware hw, SensorData data)
    {
        var coreTemps = new Dictionary<int, float>();
        var coreLoads = new Dictionary<int, float>();

        foreach (var s in hw.Sensors)
        {
            if (s.Value is null || s.Value.Value == 0f) continue;
            float v = s.Value.Value;

            switch (s.SensorType)
            {
                case SensorType.Load:
                {
                    int key = CoreKey(ThreadSuffixRe.Replace(s.Name, ""));
                    if (key >= 0 && _coreMap.TryGetValue(key, out var li))
                        coreLoads[li.displayId] = Math.Max(v, coreLoads.GetValueOrDefault(li.displayId));
                    break;
                }

                case SensorType.Temperature:
                    string n = s.Name;
                    bool isCcd = n.Contains("CCD") || n.Contains("Ccd");

                    // Per-core temps (Intel "Core #N", "P-Core #N", "E-Core #N")
                    int tkey = isCcd ? -1 : CoreKey(n);
                    if (tkey >= 0 && _coreMap.TryGetValue(tkey, out var ti))
                        coreTemps[ti.displayId] = v;

                    // CPU package/die temp — AMD and Intel variants
                    if (!isCcd && (n.Contains("Tctl") || n.Contains("Tdie") || n.Contains("Package") ||
                                   n.Contains("Average") || n == "CPU Core" || n == "Core Average"))
                        data.cpu_temp ??= v;

                    // Intel per-core temps as fallback
                    if (data.cpu_temp == null && !isCcd && v > 20 && v < 115)
                        data.cpu_temp ??= v;

                    // CCD temps (AMD)
                    if (isCcd)
                    {
                        if (n.Contains("1")) data.ccd0_temp ??= v;
                        else if (n.Contains("2")) data.ccd1_temp ??= v;
                    }
                    break;

                case SensorType.Power:
                    if (s.Name.Contains("Package") || s.Name == "CPU Power" ||
                        s.Name == "Total" || s.Name.Contains("TDP"))
                        data.pkg_power ??= v;
                    break;

                case SensorType.Clock:
                {
                    int mapKey = CoreKey(s.Name);
                    if (mapKey >= 0 && _coreMap.TryGetValue(mapKey, out var cinfo))
                        data.cores[cinfo.displayId] = new CoreInfo
                        {
                            clk  = v, rank = cinfo.rank,
                            kind = !_hybrid ? null : mapKey >= 1000 ? "E" : "P"
                        };
                    break;
                }
            }
        }

        foreach (var (id, core) in data.cores)
        {
            if (coreTemps.TryGetValue(id, out float t)) core.temp = t;
            if (coreLoads.TryGetValue(id, out float l)) core.load = l;
        }
    }

    private static void ReadGpu(IHardware hw, SensorData data)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.Value is null || s.Value.Value == 0f) continue;
            float v = s.Value.Value;
            switch (s.SensorType)
            {
                case SensorType.Temperature: if (s.Name == "GPU Core") data.gpu_temp    ??= v; break;
                case SensorType.Voltage:     if (s.Name == "GPU Core") data.gpu_voltage ??= v; break;
                case SensorType.Power:       if (s.Name.Contains("Package") || s.Name.Contains("Power")) data.gpu_power ??= v; break;
                case SensorType.Clock:       if (s.Name == "GPU Core") data.gpu_clock   ??= v; break;
                case SensorType.Load:        if (s.Name == "GPU Core" || s.Name == "D3D 3D" || s.Name.Contains("GPU Core")) data.gpu_load ??= v; break;
            }
        }
    }

    private static string CleanCpuName(string name) =>
        name.Replace("AMD ", "").Replace("Intel® Core™ ", "")
            .Replace("Intel® ", "").Replace("Intel ", "")
            .Replace("  ", " ").Trim();

    private static string CleanGpuName(string name) =>
        name.Replace("AMD Radeon", "").Replace("NVIDIA GeForce", "")
            .Replace("Intel® Arc™", "Arc").Replace("Intel Arc", "Arc")
            .Replace("(TM)", "").Trim();

    private bool _paused = false;
    public void PausePoll(int ms)
    {
        _paused = true;
        Console.WriteLine($"[hw] Poll paused for {ms}ms");
        Task.Delay(ms).ContinueWith(_ => { _paused = false; Console.WriteLine("[hw] Poll resumed"); });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        _computer.Close();
        _perfCounter?.Dispose();
    }
}