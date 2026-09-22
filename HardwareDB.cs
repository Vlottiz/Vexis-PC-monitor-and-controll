using System.Management;

/// <summary>
/// HardwareDB — cross-references four universality sources:
///
///   1. PCI-IDs     GPU identification by VID:DID (pci-ids.ucw.cz)
///   2. USB-IDs     RGB/HID device identification (linux-usb.org/usb-ids)
///   3. hwmon       SuperIO chip → sensor config (linux/drivers/hwmon/)
///   4. SMBIOS/WMI  Board, BIOS, chassis type — zero-driver universal fallback
///
/// Priority: monitoring accuracy → fan control → RGB identification → cosmetics
/// </summary>
public static class HardwareDB
{
    // ════════════════════════════════════════════════════════════════════════
    // 1. PCI-IDs  —  GPU identification
    //    Source: https://pci-ids.ucw.cz/v2.2/pci.ids  (public domain)
    //    Subset: AMD RDNA2/3/4, Nvidia Ampere/Ada/Blackwell, Intel Arc
    //    Key format: "VVVV:DDDD" (vendor:device, all lowercase hex)
    // ════════════════════════════════════════════════════════════════════════

    private static readonly Dictionary<string, string> GpuPciNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── AMD Radeon RX 6000 (RDNA 2) ─────────────────────────────────
        ["1002:73bf"] = "Radeon RX 6900 XT",
        ["1002:73a5"] = "Radeon RX 6950 XT",
        ["1002:73bf"] = "Radeon RX 6900 XT",
        ["1002:73a3"] = "Radeon RX 6800 XT",
        ["1002:73ab"] = "Radeon RX 6800",
        ["1002:73df"] = "Radeon RX 6700 XT",
        ["1002:73ff"] = "Radeon RX 6600 XT",
        ["1002:73e3"] = "Radeon RX 6650 XT",
        ["1002:73ef"] = "Radeon RX 6700",
        ["1002:7422"] = "Radeon RX 6500 XT",
        ["1002:7421"] = "Radeon RX 6400",
        ["1002:743f"] = "Radeon RX 6750 XT",

        // ── AMD Radeon RX 7000 (RDNA 3) ─────────────────────────────────
        ["1002:744c"] = "Radeon RX 7900 XTX",
        ["1002:7448"] = "Radeon RX 7900 XT",
        ["1002:744e"] = "Radeon RX 7900 GRE",
        ["1002:7470"] = "Radeon RX 7800 XT",
        ["1002:7471"] = "Radeon RX 7700 XT",
        ["1002:7480"] = "Radeon RX 7600",
        ["1002:7483"] = "Radeon RX 7600 XT",

        // ── AMD Radeon RX 9000 (RDNA 4) ─────────────────────────────────
        ["1002:7550"] = "Radeon RX 9070 XT",
        ["1002:7552"] = "Radeon RX 9070",
        ["1002:7560"] = "Radeon RX 9060 XT",

        // ── AMD Integrated (for filtering, not display) ──────────────────
        ["1002:13c0"] = "Radeon Graphics (Integrated)",   // common RDNA3 iGPU
        ["1002:164e"] = "Radeon Graphics (Integrated)",
        ["1002:1681"] = "Radeon Graphics (Integrated)",

        // ── Nvidia RTX 3000 (Ampere) ─────────────────────────────────────
        ["10de:2204"] = "GeForce RTX 3090",
        ["10de:2208"] = "GeForce RTX 3080 Ti",
        ["10de:2206"] = "GeForce RTX 3080",
        ["10de:220a"] = "GeForce RTX 3080 12GB",
        ["10de:2216"] = "GeForce RTX 3080 Ti",
        ["10de:2484"] = "GeForce RTX 3070",
        ["10de:2488"] = "GeForce RTX 3070 Ti",
        ["10de:2504"] = "GeForce RTX 3060",
        ["10de:2507"] = "GeForce RTX 3060 Ti",
        ["10de:2503"] = "GeForce RTX 3060 Ti",
        ["10de:2571"] = "GeForce RTX 3050",
        ["10de:2414"] = "GeForce RTX 3090 Ti",

        // ── Nvidia RTX 4000 (Ada Lovelace) ───────────────────────────────
        ["10de:2684"] = "GeForce RTX 4090",
        ["10de:2702"] = "GeForce RTX 4080 Super",
        ["10de:2704"] = "GeForce RTX 4080",
        ["10de:2782"] = "GeForce RTX 4070 Ti Super",
        ["10de:2705"] = "GeForce RTX 4070 Ti",
        ["10de:2786"] = "GeForce RTX 4070 Super",
        ["10de:2783"] = "GeForce RTX 4070",
        ["10de:2860"] = "GeForce RTX 4060 Ti",
        ["10de:2882"] = "GeForce RTX 4060",

        // ── Nvidia RTX 5000 (Blackwell) ───────────────────────────────────
        ["10de:2b85"] = "GeForce RTX 5090",
        ["10de:2b87"] = "GeForce RTX 5080",
        ["10de:2c03"] = "GeForce RTX 5070 Ti",
        ["10de:2c05"] = "GeForce RTX 5070",
        ["10de:2c07"] = "GeForce RTX 5060 Ti",

        // ── Intel Arc (Xe HPG) ────────────────────────────────────────────
        ["8086:56a0"] = "Arc A770",
        ["8086:56a1"] = "Arc A750",
        ["8086:56a5"] = "Arc A380",
        ["8086:56a6"] = "Arc A310",
        ["8086:5690"] = "Arc B580",
        ["8086:5691"] = "Arc B570",
    };

    /// <summary>
    /// Look up a friendly GPU name by PCI VID:DID.
    /// Returns null if not in database — caller should use LHM name as fallback.
    /// </summary>
    public static string? LookupGpuName(ushort vendorId, ushort deviceId)
    {
        string key = $"{vendorId:x4}:{deviceId:x4}";
        return GpuPciNames.TryGetValue(key, out var name) ? name : null;
    }

    /// <summary>
    /// Returns true if this PCI device is a discrete (non-integrated) GPU.
    /// Helps filter out iGPU from the primary GPU display.
    /// </summary>
    public static bool IsDiscreteGpu(ushort vendorId, ushort deviceId)
    {
        string key = $"{vendorId:x4}:{deviceId:x4}";
        if (GpuPciNames.TryGetValue(key, out var name))
            return !name.Contains("Integrated");
        // Unknown device — assume discrete if from a GPU vendor
        return vendorId is 0x1002 or 0x10DE or 0x8086;
    }

    // ════════════════════════════════════════════════════════════════════════
    // 2. USB-IDs  —  HID / RGB device identification
    //    Source: https://www.linux-usb.org/usb-ids.html  (public domain)
    //    Subset: common gaming peripherals and RGB controllers
    //    Key format: "VVVV:PPPP" (vendor:product, lowercase hex)
    // ════════════════════════════════════════════════════════════════════════

    private static readonly Dictionary<string, string> UsbDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── MSI RGB controllers ───────────────────────────────────────────
        ["0db0:0076"] = "MSI Mystic Light (ARGB headers)",
        ["0db0:cd0e"] = "MSI USB Audio / Hub",

        // ── Corsair ───────────────────────────────────────────────────────
        ["1b1c:0c20"] = "Corsair iCUE Link System Hub",
        ["1b1c:0c1a"] = "Corsair Commander Pro",
        ["1b1c:0c10"] = "Corsair Lighting Node Pro",
        ["1b1c:1b65"] = "Corsair K95 RGB Platinum",
        ["1b1c:1b49"] = "Corsair K70 RGB MK.2",

        // ── Logitech ─────────────────────────────────────────────────────
        ["046d:c545"] = "Logitech G915 Wireless",
        ["046d:c539"] = "Logitech G915 TKL",
        ["046d:c33f"] = "Logitech G910",
        ["046d:c088"] = "Logitech G502 Hero",

        // ── Razer ─────────────────────────────────────────────────────────
        ["1532:0255"] = "Razer BlackWidow V4 Pro",
        ["1532:0232"] = "Razer BlackWidow Elite",
        ["1532:0084"] = "Razer DeathAdder Elite",
        ["1532:00c2"] = "Razer Viper Ultimate",

        // ── ASUS ROG / Aura ───────────────────────────────────────────────
        ["0b05:1866"] = "ASUS ROG Aura Terminal",
        ["0b05:18f3"] = "ASUS ROG Aura USB Hub",
        ["0b05:19af"] = "ASUS Aura Addressable Gen 2",

        // ── SteelSeries ───────────────────────────────────────────────────
        ["1038:1612"] = "SteelSeries Apex Pro",
        ["1038:1729"] = "SteelSeries Arctis Nova Pro",

        // ── HyperX / Kingston ─────────────────────────────────────────────
        ["0951:16d8"] = "HyperX Alloy Origins",
        ["0951:16e5"] = "HyperX Pulsefire Haste",

        // ── Gigabyte RGB ──────────────────────────────────────────────────
        ["048d:5702"] = "Gigabyte RGB Fusion Controller",
        ["048d:8297"] = "Gigabyte RGB Fusion 2.0",

        // ── ASRock ────────────────────────────────────────────────────────
        ["26ce:01a2"] = "ASRock Polychrome RGB Controller",

        // ── NZXT ──────────────────────────────────────────────────────────
        ["1e71:2001"] = "NZXT Kraken X",
        ["1e71:2006"] = "NZXT Kraken Z",
        ["1e71:1011"] = "NZXT Smart Device V2",
    };

    /// <summary>Look up a friendly device name by USB VID:PID.</summary>
    public static string? LookupUsbDevice(ushort vendorId, ushort productId)
    {
        string key = $"{vendorId:x4}:{productId:x4}";
        return UsbDeviceNames.TryGetValue(key, out var name) ? name : null;
    }

    // ════════════════════════════════════════════════════════════════════════
    // 3. hwmon chip database
    //    Source: Linux kernel drivers/hwmon/ (GPL — used as reference only)
    //    Chips: ITE IT8xxx, Nuvoton NCT6xxx, Winbond W83xxx, Fintek F71xxx
    //
    //    For each chip we document:
    //      - fanCount     : number of PWM/tach fan headers
    //      - tempCount    : number of temperature sensors
    //      - fanNames     : expected LHM sensor names for fans
    //      - notes        : quirks relevant to PCMonitor fan control
    // ════════════════════════════════════════════════════════════════════════

    public record ChipConfig(
        int    FanCount,
        int    TempCount,
        string[] FanNames,
        string[] TempNames,
        string Notes
    );

    private static readonly Dictionary<string, ChipConfig> ChipConfigs = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── ITE IT8689E  (MSI X570, B550, Z490, X870, B650) ─────────────
        ["ITE IT8689E"] = new(7, 3,
            ["Fan #1","Fan #2","Fan #3","Fan #4","Fan #5","Fan #6","Fan #7"],
            ["Temperature #1","Temperature #2","Temperature #3"],
            "7 fan headers. Fan #1=CPU_FAN, #2=PUMP_FAN, #3-7=SYS_FAN. Common on MSI AM4/AM5 boards."),

        // ── ITE IT8795E  (MSI Z390, X299) ─────────────────────────────
        ["ITE IT8795E"] = new(5, 3,
            ["Fan #1","Fan #2","Fan #3","Fan #4","Fan #5"],
            ["Temperature #1","Temperature #2","Temperature #3"],
            "5 fan headers. MSI Intel boards."),

        // ── ITE IT8665E  (ASRock X570, B550) ────────────────────────────
        ["ITE IT8665E"] = new(6, 3,
            ["Fan #1","Fan #2","Fan #3","Fan #4","Fan #5","Fan #6"],
            ["Temperature #1","Temperature #2","Temperature #3"],
            "6 fan headers. Common on ASRock AM4 boards."),

        // ── ITE IT8613E  (ASRock Z790, X670) ─────────────────────────────
        ["ITE IT8613E"] = new(7, 3,
            ["Fan #1","Fan #2","Fan #3","Fan #4","Fan #5","Fan #6","Fan #7"],
            ["Temperature #1","Temperature #2","Temperature #3"],
            "7 fan headers. ASRock Intel/AM5 boards."),

        // ── Nuvoton NCT6798D  (ASUS ROG Z490, Z590, B550, X570) ─────────
        ["Nuvoton NCT6798D"] = new(7, 9,
            ["Fan #1","Fan #2","Fan #3","Fan #4","Fan #5","Fan #6","Fan #7"],
            ["CPU Core","CPU","System","Auxiliary","VRM MOS","PCH","Temperature #7","Temperature #8","Temperature #9"],
            "9 temp sensors, 7 fan headers. ASUS primary SuperIO."),

        // ── Nuvoton NCT6796D  (Gigabyte Z490, B550, X570) ───────────────
        ["Nuvoton NCT6796D"] = new(6, 9,
            ["Fan #1","Fan #2","Fan #3","Fan #4","Fan #5","Fan #6"],
            ["System","CPU","Auxiliary","Temperature #4","Temperature #5","Temperature #6","Temperature #7","Temperature #8","Temperature #9"],
            "6 fan headers. Common on Gigabyte boards."),

        // ── Nuvoton NCT6779D  (older Gigabyte Z370, B450) ────────────────
        ["Nuvoton NCT6779D"] = new(5, 9,
            ["Fan #1","Fan #2","Fan #3","Fan #4","Fan #5"],
            ["System","CPU","Auxiliary","Temperature #4","Temperature #5","Temperature #6","Temperature #7","Temperature #8","Temperature #9"],
            "5 fan headers. Older Gigabyte/MSI boards."),

        // ── Nuvoton NCT6775F  (ASUS Z170, X99 era) ───────────────────────
        ["Nuvoton NCT6775F"] = new(4, 9,
            ["Fan #1","Fan #2","Fan #3","Fan #4"],
            ["CPU Core","CPU","System","Auxiliary","Temperature #5","Temperature #6","Temperature #7","Temperature #8","Temperature #9"],
            "4 fan headers. Older ASUS boards."),

        // ── Winbond W83627DHG  (generic older boards) ────────────────────
        ["Winbond W83627DHG"] = new(3, 3,
            ["Fan #1","Fan #2","Fan #3"],
            ["System","CPU","Auxiliary"],
            "3 fan headers. Found in older/budget boards."),

        // ── Nuvoton NCT6687D-R  (MSI X870E, B650, newer AMD/Intel) ────────────
        ["Nuvoton NCT6687D-R"] = new(11, 8,
            ["CPU Fan","Pump Fan #1","Chipset Fan","System Fan #1","System Fan #2",
             "System Fan #3","System Fan #4","System Fan #5","System Fan #6",
             "EZ-Connect Fan","GPU Fan"],
            ["Temperature #1","CPU","System","PCH","VRM MOS","Temperature #5",
             "Temperature #6","Temperature #7"],
            "11 fan headers. MSI X870E TOMAHAWK, B650, Z790 boards. Pump, EZ-Connect and GPU Fan headers included."),

        // ── Fintek F71889AD  (some ASRock) ───────────────────────────────
        ["Fintek F71889AD"] = new(4, 3,
            ["Fan #1","Fan #2","Fan #3","Fan #4"],
            ["Temperature #1","Temperature #2","Temperature #3"],
            "4 fan headers. Found on some ASRock boards."),
    };

    /// <summary>
    /// Get the chip config for a SuperIO chip name from LHM.
    /// Tries exact match first, then partial match (handles name variants).
    /// </summary>
    public static ChipConfig? GetChipConfig(string chipName)
    {
        if (string.IsNullOrEmpty(chipName)) return null;
        if (ChipConfigs.TryGetValue(chipName.Trim(), out var exact)) return exact;
        // Partial match — LHM sometimes appends rev codes
        foreach (var kv in ChipConfigs)
            if (chipName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Contains(chipName.Split(' ').LastOrDefault() ?? "", StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return null;
    }

    /// <summary>
    /// Returns how many fan headers this chip is expected to have.
    /// Used to pre-populate the fan controller with the right number of slots.
    /// Returns -1 if chip is unknown.
    /// </summary>
    public static int GetExpectedFanCount(string chipName) =>
        GetChipConfig(chipName)?.FanCount ?? -1;

    // ════════════════════════════════════════════════════════════════════════
    // 4. SMBIOS / WMI  —  universal zero-driver hardware detection
    //    Works on every Windows 10/11 PC, no admin required for basic queries.
    //    Admin IS required for some queries (hardware events, raw SMBIOS).
    // ════════════════════════════════════════════════════════════════════════

    public enum ChassisType
    {
        Unknown, Desktop, Laptop, Notebook, Tablet, AIO, MiniPC, Server, Other
    }

    private static readonly Dictionary<int, ChassisType> ChassisTypeCodes = new()
    {
        [1]  = ChassisType.Other,
        [2]  = ChassisType.Unknown,
        [3]  = ChassisType.Desktop,
        [4]  = ChassisType.Desktop,      // Low Profile Desktop
        [5]  = ChassisType.Desktop,      // Pizza Box
        [6]  = ChassisType.MiniPC,       // Mini Tower
        [7]  = ChassisType.Desktop,      // Tower
        [8]  = ChassisType.Desktop,      // Portable
        [9]  = ChassisType.Laptop,
        [10] = ChassisType.Notebook,
        [11] = ChassisType.Tablet,       // Hand Held
        [12] = ChassisType.Other,        // Docking Station
        [13] = ChassisType.AIO,          // All in One
        [14] = ChassisType.Other,        // Sub Notebook
        [15] = ChassisType.Other,        // Space-saving
        [16] = ChassisType.MiniPC,       // Lunch Box
        [17] = ChassisType.Server,
        [18] = ChassisType.Other,        // Expansion Chassis
        [21] = ChassisType.AIO,
        [30] = ChassisType.Tablet,
        [31] = ChassisType.MiniPC,       // NUC / Mini PC
        [32] = ChassisType.MiniPC,
    };

    public record SmBiosInfo(
        string Board,          // Product name  e.g. "MAG X870E TOMAHAWK WIFI"
        string BoardMfr,       // Manufacturer  e.g. "MSI"
        string BiosVersion,    // SMBIOS BIOS   e.g. "1.C0"
        string SystemFamily,   // OEM family    e.g. "ROG", "ThinkPad"
        ChassisType Chassis,   // Form factor
        bool   HasBattery,     // True for laptops/tablets
        int    MemSlots,       // Physical RAM slots
        int    MemSlotsUsed    // Populated slots
    );

    /// <summary>
    /// Read all useful SMBIOS data in one pass.
    /// No drivers required — pure WMI.
    /// </summary>
    public static SmBiosInfo ReadSmbios()
    {
        string board = "", boardMfr = "", bios = "", family = "";
        ChassisType chassis = ChassisType.Unknown;
        bool hasBattery = false;
        int memSlots = 0, memSlotsUsed = 0;

        try
        {
            using var q = new ManagementObjectSearcher(
                "SELECT Manufacturer,Product,Version FROM Win32_BaseBoard");
            foreach (ManagementObject o in q.Get())
            {
                boardMfr = o["Manufacturer"]?.ToString()?.Trim() ?? "";
                board    = o["Product"]?.ToString()?.Trim() ?? "";
                break;
            }
        } catch { }

        try
        {
            using var q = new ManagementObjectSearcher(
                "SELECT SMBIOSBIOSVersion,Manufacturer FROM Win32_BIOS");
            foreach (ManagementObject o in q.Get())
            {
                bios = o["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "";
                break;
            }
        } catch { }

        try
        {
            using var q = new ManagementObjectSearcher(
                "SELECT ChassisTypes,PoweredOn FROM Win32_SystemEnclosure");
            foreach (ManagementObject o in q.Get())
            {
                var types = o["ChassisTypes"] as ushort[];
                if (types?.Length > 0 && ChassisTypeCodes.TryGetValue(types[0], out var ct))
                    chassis = ct;
                break;
            }
        } catch { }

        try
        {
            using var q = new ManagementObjectSearcher(
                "SELECT SystemFamily FROM Win32_ComputerSystem");
            foreach (ManagementObject o in q.Get())
            {
                family = o["SystemFamily"]?.ToString()?.Trim() ?? "";
                break;
            }
        } catch { }

        // Battery presence = laptop/tablet
        try
        {
            using var q = new ManagementObjectSearcher(
                "SELECT EstimatedChargeRemaining FROM Win32_Battery");
            hasBattery = q.Get().Count > 0;
            if (hasBattery && chassis == ChassisType.Unknown)
                chassis = ChassisType.Laptop;
        } catch { }

        // RAM slot topology
        try
        {
            using var q = new ManagementObjectSearcher(
                "SELECT MemoryType FROM Win32_PhysicalMemoryArray");
            foreach (ManagementObject o in q.Get())
                memSlots += Convert.ToInt32(o["MemoryType"] ?? 0);
        } catch { }

        try
        {
            using var q = new ManagementObjectSearcher(
                "SELECT Capacity FROM Win32_PhysicalMemory");
            foreach (ManagementObject o in q.Get())
                if (Convert.ToInt64(o["Capacity"] ?? 0) > 0) memSlotsUsed++;
        } catch { }

        return new SmBiosInfo(board, boardMfr, bios, family,
                              chassis, hasBattery, memSlots, memSlotsUsed);
    }

    // ════════════════════════════════════════════════════════════════════════
    // WMI fallback helpers — used by SensorService when LHM finds nothing
    // ════════════════════════════════════════════════════════════════════════

    public record WmiFallbackData(
        float?  CpuTempC,      // Win32_PerfFormattedData_Counters_ThermalZoneInformation
        float?  CpuLoadPct,    // Win32_Processor.LoadPercentage
        float?  GpuTempC,      // Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine (Win11)
        float?  GpuLoadPct,    // same source
        string  CpuName,       // Win32_Processor.Name
        string  GpuName,       // Win32_VideoController.Caption
        int     GpuVramMb,     // Win32_VideoController.AdapterRAM
        float   RamUsedGb,     // Win32_OperatingSystem free vs total
        float   RamTotalGb
    );

    /// <summary>
    /// Pure WMI sensor fallback — no drivers required.
    /// Slower than LHM (~50-100ms) so call only when LHM data is missing.
    /// CPU temp via WMI is unreliable on many systems — use as last resort.
    /// </summary>
    public static WmiFallbackData ReadWmiFallback()
    {
        float? cpuTemp = null, cpuLoad = null, gpuTemp = null, gpuLoad = null;
        string cpuName = "", gpuName = "";
        int gpuVram = 0;
        float ramUsed = 0, ramTotal = 0;

        // CPU load (reliable)
        try
        {
            using var q = new ManagementObjectSearcher(
                "SELECT Name,LoadPercentage FROM Win32_Processor");
            foreach (ManagementObject o in q.Get())
            {
                cpuName = o["Name"]?.ToString()?.Trim() ?? "";
                if (o["LoadPercentage"] != null)
                    cpuLoad = Convert.ToSingle(o["LoadPercentage"]);
                break;
            }
        } catch { }

        // CPU temp via ACPI thermal zone (unreliable on many systems, but works on some laptops)
        try
        {
            using var q = new ManagementObjectSearcher("root\\wmi",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            float best = 0;
            foreach (ManagementObject o in q.Get())
            {
                float raw = Convert.ToSingle(o["CurrentTemperature"]);
                float c = (raw / 10f) - 273.15f;
                if (c > 20 && c < 115 && c > best) best = c;
            }
            if (best > 0) cpuTemp = best;
        } catch { }

        // GPU info and VRAM
        try
        {
            using var q = new ManagementObjectSearcher(
                "SELECT Caption,AdapterRAM FROM Win32_VideoController");
            long bestVram = 0;
            foreach (ManagementObject o in q.Get())
            {
                string name = o["Caption"]?.ToString()?.Trim() ?? "";
                long vram = Convert.ToInt64(o["AdapterRAM"] ?? 0);
                // Skip Microsoft Basic / RemoteFX adapters
                if (name.Contains("Microsoft") || name.Contains("Remote")) continue;
                if (vram > bestVram)
                {
                    bestVram = vram;
                    gpuName  = name;
                    gpuVram  = (int)(vram / (1024L * 1024));
                }
            }
        } catch { }

        // RAM usage
        try
        {
            using var q = new ManagementObjectSearcher(
                "SELECT FreePhysicalMemory,TotalVisibleMemorySize FROM Win32_OperatingSystem");
            foreach (ManagementObject o in q.Get())
            {
                float totalKb = Convert.ToSingle(o["TotalVisibleMemorySize"]);
                float freeKb  = Convert.ToSingle(o["FreePhysicalMemory"]);
                ramTotal = totalKb / (1024f * 1024f);
                ramUsed  = (totalKb - freeKb) / (1024f * 1024f);
                break;
            }
        } catch { }

        return new WmiFallbackData(cpuTemp, cpuLoad, gpuTemp, gpuLoad,
                                   cpuName, gpuName, gpuVram, ramUsed, ramTotal);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Utility: board manufacturer → likely SuperIO family
    // Helps narrow sensor name search before LHM identifies the actual chip
    // ════════════════════════════════════════════════════════════════════════

    public static string GuessChipFamily(string boardMfr) => boardMfr.ToLowerInvariant() switch
    {
        var s when s.Contains("msi")      => "ITE",      // MSI almost exclusively uses ITE
        var s when s.Contains("asrock")   => "ITE",      // ASRock uses ITE for most boards
        var s when s.Contains("asus")     => "Nuvoton",  // ASUS uses Nuvoton NCT6xxx
        var s when s.Contains("gigabyte") => "Nuvoton",  // Gigabyte uses Nuvoton NCT6xxx
        var s when s.Contains("evga")     => "Nuvoton",
        var s when s.Contains("biostar")  => "ITE",
        _                                  => "Unknown"
    };
}
