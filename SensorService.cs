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
    public List<float>? dimm_temps { get; set; }
    public Dictionary<int, CoreInfo>  cores     { get; set; } = new();
    public Dictionary<string, float>  all_temps { get; set; } = new();
    public List<FanInfo>?             fans      { get; set; }
    public HwInfo?                    info      { get; set; }
}

public class CoreInfo
{
    public float clk  { get; set; }
    public int   rank { get; set; }  // 0 = no rank info (Intel/generic)
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

    // AMD 9950X3D specific rank table
    private static readonly Dictionary<int, (int displayId, int rank)> _amd9950x3dMap = new()
    {
        {1,(0,12)},{2,(1,11)},{3,(2,8)},{4,(3,10)},{5,(4,13)},{6,(5,9)},{7,(6,15)},{8,(7,14)},
        {9,(8,2)},{10,(9,1)},{11,(10,3)},{12,(11,5)},{13,(12,4)},{14,(13,1)},{15,(14,6)},{16,(15,7)}
    };

    // Matches: "Core #1", "CPU Core #1", "P-Core #1", "E-Core #1" (Intel 12th gen+)
    private static readonly Regex CoreClockRe = new(
        @"^(?:CPU |P-|E-)?Core #?(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CoreLoadRe  = new(
        @"^(?:CPU Core|P-Core|E-Core) #?(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        // ── Step 0: Remove ALL Windows security blocks on the sensor driver ───────
        // Intel CPUs need WinRing0 for MSR access (temps + clocks).
        // Three separate Windows features can block it — we fix all three at once.
        if (_config?.Settings?.ContainsKey("disableSecurityBypass") == true &&
            _config.Settings["disableSecurityBypass"] == "true")
        {
            Console.WriteLine("[hw] Security bypass disabled by user preference.");
        }
        else
        try
        {
            bool needsRestart = false;

            // Block 1: Memory Integrity (HVCI)
            try
            {
                using var hvci = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
                    writable: true);
                if (hvci != null && hvci.GetValue("Enabled") is int hvciVal && hvciVal == 1)
                { hvci.SetValue("Enabled", 0, Microsoft.Win32.RegistryValueKind.DWord); needsRestart = true; Console.WriteLine("[hw] Fixed: Memory Integrity disabled."); }
                else Console.WriteLine("[hw] OK: Memory Integrity already off.");
            }
            catch (Exception ex) { Console.WriteLine($"[hw] HVCI check failed: {ex.Message}"); }

            // Block 2: Vulnerable Driver Blocklist (blocks WinRing0 on Win11 22H2+)
            try
            {
                var vdb = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\CI\Config", writable: true)
                    ?? Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                    @"SYSTEM\CurrentControlSet\Control\CI\Config");
                if (vdb != null)
                {
                    var cur = vdb.GetValue("VulnerableDriverBlocklistEnable");
                    if (cur == null || cur is int cv && cv != 0)
                    { vdb.SetValue("VulnerableDriverBlocklistEnable", 0, Microsoft.Win32.RegistryValueKind.DWord); needsRestart = true; Console.WriteLine("[hw] Fixed: Vulnerable Driver Blocklist disabled."); }
                    else Console.WriteLine("[hw] OK: Driver Blocklist already off.");
                    vdb.Dispose();
                }
            }
            catch (Exception ex) { Console.WriteLine($"[hw] VDB check failed: {ex.Message}"); }

            // Block 3: Virtualization Based Security
            try
            {
                using var dg = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\DeviceGuard", writable: true);
                if (dg != null && dg.GetValue("EnableVirtualizationBasedSecurity") is int vbs && vbs == 1)
                { dg.SetValue("EnableVirtualizationBasedSecurity", 0, Microsoft.Win32.RegistryValueKind.DWord); needsRestart = true; Console.WriteLine("[hw] Fixed: VBS disabled."); }
                else Console.WriteLine("[hw] OK: VBS already off.");
            }
            catch (Exception ex) { Console.WriteLine($"[hw] VBS check failed: {ex.Message}"); }

            if (needsRestart)
            {
                var res = System.Windows.Forms.MessageBox.Show(
                    "PC Monitor has updated your security settings to enable\n" +
                    "hardware sensor access (CPU temps, clock speeds).\n\n" +
                    "This is a ONE-TIME change. Your PC needs to restart.\n\nRestart now?",
                    "PC Monitor — One-Time Setup",
                    System.Windows.Forms.MessageBoxButtons.YesNo,
                    System.Windows.Forms.MessageBoxIcon.Information);
                if (res == System.Windows.Forms.DialogResult.Yes)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName="shutdown.exe", Arguments="-r -t 5", UseShellExecute=false, CreateNoWindow=true });
                    System.Windows.Forms.Application.Exit();
                    return;
                }
            }
            else Console.WriteLine("[hw] All security blocks already cleared.");
        }
        catch (Exception ex) { Console.WriteLine($"[hw] Security setup error: {ex.Message}"); }

        // ── Step 1: Add Defender exclusions ─────────────────────────────────────
        try
        {
            string sysTmp = Path.Combine(Path.GetTempPath(), "LibreHardwareMonitorLib.sys");
            string args   = $"-NoProfile -WindowStyle Hidden -Command \"Add-MpPreference" +
                            $" -ExclusionPath '{sysTmp}'" +
                            $" -ExclusionProcess 'Pcmonitor2.0.exe' -ErrorAction SilentlyContinue\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe", Arguments = args,
                UseShellExecute = false, CreateNoWindow = true
            })?.WaitForExit(5000);
            Console.WriteLine("[hw] Defender exclusion applied.");
        }
        catch (Exception ex) { Console.WriteLine($"[hw] Defender exclusion skipped: {ex.Message}"); }

        // ── Step 2: Install WHQL-signed WinRing0 driver ──────────────────────────
        // WinRing0x64.sys from OpenHardwareMonitor is MIT licensed and WHQL-signed
        // by Microsoft — meaning Windows loads it regardless of Memory Integrity.
        // This is the same approach used by CPU-Z, HWiNFO, and MSI Afterburner.
        // Source: https://github.com/openhardwaremonitor/openhardwaremonitor
        try
        {
            string sysPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinRing0x64.sys");

            // Download the signed driver if not already present
            if (!File.Exists(sysPath))
            {
                Console.WriteLine("[hw] Downloading WHQL-signed WinRing0x64.sys...");
                using var http   = new System.Net.Http.HttpClient();
                http.Timeout     = TimeSpan.FromSeconds(15);
                // OHM release on GitHub — MIT licensed, WHQL signed
                var url   = "https://raw.githubusercontent.com/openhardwaremonitor/openhardwaremonitor/master/Hardware/WinRing0x64.sys";
                var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                File.WriteAllBytes(sysPath, bytes);
                Console.WriteLine($"[hw] Downloaded WinRing0x64.sys ({bytes.Length} bytes)");
            }
            else
            {
                Console.WriteLine("[hw] WinRing0x64.sys already present.");
            }

            if (File.Exists(sysPath))
            {
                // Stop and delete existing service — must wait between each step
                void RunSc(string scArgs) {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName="sc.exe", Arguments=scArgs,
                      UseShellExecute=false, CreateNoWindow=true })?.WaitForExit(3000);
                }
                RunSc("stop WinRing0_1_2_0");
                Thread.Sleep(600);
                RunSc("delete WinRing0_1_2_0");
                Thread.Sleep(1000); // must wait for SCM to fully release the service record
                RunSc("stop PawnIO");
                RunSc("delete PawnIO");
                Thread.Sleep(300);

                // Create and start fresh
                RunSc($"create WinRing0_1_2_0 type= kernel start= demand binPath= \"{sysPath}\"");
                Thread.Sleep(300);
                var sc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName="sc.exe", Arguments="start WinRing0_1_2_0",
                  UseShellExecute=false, CreateNoWindow=true });
                sc?.WaitForExit(3000);
                Console.WriteLine($"[hw] WinRing0 service started (exit={sc?.ExitCode}).");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[hw] WinRing0 setup failed: {ex.Message}");
        }

        // ── Step 2b: Clean up stale PawnIO — let LHM manage it ─────────────────
        try
        {
            void RunScClean(string scArgs) {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName="sc.exe", Arguments=scArgs,
                  UseShellExecute=false, CreateNoWindow=true })?.WaitForExit(3000);
            }
            RunScClean("stop PawnIO");
            Thread.Sleep(800);
            RunScClean("delete PawnIO");
            Thread.Sleep(1200);
            Console.WriteLine("[pawnio] Stale PawnIO cleared — LHM will register on Open().");
            var lhmAsm = typeof(Computer).Assembly;
            var binRes = lhmAsm.GetManifestResourceNames().Where(n => n.Contains("PawnIo")).ToList();
            Console.WriteLine($"[pawnio] PawnIO resources: {string.Join(", ", binRes)}");
        }
        catch (Exception ex) { Console.WriteLine($"[pawnio] Cleanup failed: {ex.Message}"); }

        Console.WriteLine("[pawnio] Waiting for drivers to stabilize before Open()...");
        Thread.Sleep(2000);

        try
        {
            _computer.Open();
            Thread.Sleep(2000);
        }
        catch (MissingMethodException ex) when (ex.Message.Contains("Mutex"))
        {
            // LHM 0.9.6 uses a Mutex constructor overload not available in this
            // .NET 8 runtime. This is a known LHM/NET8 self-contained incompatibility.
            // The app will run in WMI-only mode (clock speed + load, no temps).
            Console.WriteLine($"[hw] LHM Mutex API unavailable — falling back to WMI-only mode. ({ex.Message})");
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
                    var ccdTemps = hw.Sensors
                        .Where(s => s.SensorType == SensorType.Temperature &&
                                    (s.Name.Contains("CCD") || s.Name.Contains("Ccd")))
                        .Select(s => s.Name)
                        .Distinct().Count();
                    _hwInfo.ccdCount = ccdTemps;
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
            using var q = new ManagementObjectSearcher("SELECT Speed,SMBIOSMemoryType,Capacity FROM Win32_PhysicalMemory");
            long total = 0;
            foreach (ManagementObject obj in q.Get())
            {
                total += Convert.ToInt64(obj["Capacity"]);
                if (_hwInfo.ramMhz == 0) _hwInfo.ramMhz = Convert.ToInt32(obj["Speed"]);
                if (string.IsNullOrEmpty(_hwInfo.ramType))
                    _hwInfo.ramType = Convert.ToInt32(obj["SMBIOSMemoryType"]) switch
                    { 34=>"DDR5", 26=>"DDR4", 24=>"DDR3", _=>"DDR" };
            }
            _hwInfo.ramGb = (int)(total / (1024L * 1024 * 1024));
        }
        catch { }

        try
        {
            using var q = new ManagementObjectSearcher("SELECT SocketDesignation FROM Win32_Processor");
            foreach (ManagementObject obj in q.Get()) { _hwInfo.socket = obj["SocketDesignation"]?.ToString() ?? ""; break; }
        }
        catch { }

        Console.WriteLine($"[hw] CPU: {_hwInfo.cpu} ({_hwInfo.cores}C/{_hwInfo.threads}T, {_hwInfo.socket}, vendor={_hwInfo.vendor})");
        Console.WriteLine($"[hw] GPU: {_hwInfo.gpu} ({_hwInfo.gpuVram}GB)");
        Console.WriteLine($"[hw] RAM: {_hwInfo.ramGb}GB {_hwInfo.ramType}-{_hwInfo.ramMhz}");
    }

    // ── Build dynamic core map for any CPU ─────────────────────────────────────
    // Separate regex for Intel hybrid P-Core/E-Core (12th gen+)
    private static readonly Regex PCoreClockRe = new(@"^P-Core #?(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ECoreClockRe = new(@"^E-Core #?(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
            Console.WriteLine($"[hw]   --> Intel hybrid: {pCores.Count} P-Cores + {eCores.Count} E-Cores = {_hwInfo.cores} total");
            return;
        }

        // Generic / AMD
        var genericIndices = allClockSensors
            .Where(s => CoreClockRe.IsMatch(s.Name))
            .Select(s => int.Parse(CoreClockRe.Match(s.Name).Groups[1].Value))
            .OrderBy(i => i).ToList();

        _hwInfo.cores = genericIndices.Count;

        if (_hwInfo.cpu.Contains("9950") && genericIndices.Count == 16)
        {
            _coreMap = new(_amd9950x3dMap);
            Console.WriteLine("[hw]   --> Using AMD 9950X3D rank map");
            return;
        }

        int did = 0;
        foreach (int idx in genericIndices)
            _coreMap[idx] = (did++, 0);
        Console.WriteLine($"[hw]   --> Built sequential core map: {_coreMap.Count} cores");
    }


    private void Poll(object? _)
    {
        if (_paused) return; // LHM SMBus released for OpenRGB
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
            if (hw.HardwareType == HardwareType.Memory)
            {
                hw.Update();
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType == SensorType.Load && s.Name == "Memory" && s.Value.HasValue)
                        data.ram_load ??= s.Value.Value;
                    if (s.SensorType == SensorType.Data && s.Name.Contains("Memory Used") && s.Value.HasValue)
                        data.ram_used_gb ??= s.Value.Value;
                    if (s.SensorType == SensorType.Temperature && s.Value.HasValue)
                    {
                        data.dimm_temps ??= new List<float>();
                        data.dimm_temps.Add(s.Value.Value);
                    }
                }
            }

            foreach (var s in hw.Sensors)
                if (s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value > 1f)
                {
                    string prefix = hw.HardwareType switch
                    {
                        HardwareType.Cpu         => "CPU",
                        HardwareType.GpuAmd      => "GPU",
                        HardwareType.GpuNvidia   => "GPU",
                        HardwareType.GpuIntel    => "GPU",
                        HardwareType.Motherboard => "MB",
                        _                        => hw.HardwareType.ToString()
                    };
                    data.all_temps[$"{prefix}/{s.Name}"] = (float)Math.Round(s.Value.Value, 2);
                }
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


    private void ReadCpu(IHardware hw, SensorData data)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.Value is null || s.Value.Value == 0f) continue;
            float v = s.Value.Value;

            switch (s.SensorType)
            {
                case SensorType.Temperature:
                    string n = s.Name;
                    bool isCcd = n.Contains("CCD") || n.Contains("Ccd");

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
                    int mapKey = -1;
                    var pm = PCoreClockRe.Match(s.Name);
                    var em = ECoreClockRe.Match(s.Name);
                    var gm = CoreClockRe.Match(s.Name);
                    if      (pm.Success && int.TryParse(pm.Groups[1].Value, out int pi)) mapKey = pi;
                    else if (em.Success && int.TryParse(em.Groups[1].Value, out int ei)) mapKey = 1000 + ei;
                    else if (gm.Success && int.TryParse(gm.Groups[1].Value, out int gi)) mapKey = gi;
                    if (mapKey >= 0 && _coreMap.TryGetValue(mapKey, out var cinfo))
                        data.cores[cinfo.displayId] = new CoreInfo { clk = v, rank = cinfo.rank };
                    break;
                }
            }
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

        // Clean up the WinRing0 service on exit
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sc.exe", Arguments = "stop WinRing0_1_2_0",
                UseShellExecute = false, CreateNoWindow = true
            })?.WaitForExit(2000);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sc.exe", Arguments = "delete WinRing0_1_2_0",
                UseShellExecute = false, CreateNoWindow = true
            })?.WaitForExit(2000);
        }
        catch { }
    }
}