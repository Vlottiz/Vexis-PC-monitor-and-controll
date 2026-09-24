using System.Management;

/// <summary>
/// Driver health check run at every launch (behind the loading screen).
/// Repairs what Vexis can fix itself (PawnIO sensor driver, WinRing0 leftovers)
/// and reports what it can't: a missing/broken graphics driver or other devices
/// Windows lists with a driver problem. Results go to the log, the Security page
/// and — when something is wrong — a tray notification.
/// </summary>
public static class StartupCheck
{
    public record Result(string name, string level, string detail); // level: ok | warn | error

    public static List<Result> Results { get; private set; } = new();
    public static DateTime     RanAt   { get; private set; }
    public static int Problems => Results.Count(r => r.level != "ok");

    // Device Manager problem codes worth reporting (22 = disabled by the user and
    // 45 = device not connected are normal, so they are skipped)
    private static readonly Dictionary<int, string> Codes = new()
    {
        [1]  = "not configured correctly",
        [3]  = "driver may be corrupted",
        [10] = "cannot start",
        [12] = "not enough free resources",
        [14] = "needs a restart",
        [18] = "drivers need reinstalling",
        [19] = "registry information damaged",
        [21] = "being removed",
        [24] = "not present or not working",
        [28] = "drivers not installed",
        [31] = "driver could not be loaded",
        [32] = "driver service disabled",
        [37] = "driver failed to initialise",
        [39] = "driver corrupted or missing",
        [40] = "driver registry entry damaged",
        [41] = "driver loaded but device not found",
        [43] = "stopped — reported a problem",
        [48] = "driver blocked by Windows",
        [52] = "driver signature could not be verified",
    };

    public static List<Result> Run(Action<string>? status = null, Action<Result>? onResult = null)
    {
        var results = new List<Result>();
        void Add(string name, string level, string detail)
        {
            var r = new Result(name, level, detail);
            results.Add(r);
            onResult?.Invoke(r);
            Console.WriteLine($"[check] {level.ToUpper(),-5} {name}: {detail}");
        }

        // 1. Leftovers from old builds (blocked by Windows, flagged by Defender)
        status?.Invoke("Cleaning up old driver files…");
        DriverSetup.CleanupLegacyWinRing0();

        // 2. PawnIO sensor driver — installed and running, repaired if not
        status?.Invoke("Checking sensor driver (PawnIO)…");
        try
        {
            bool ok = DriverSetup.EnsurePawnIo();
            string svc = DriverSetup.PawnIoServiceState();
            if (ok && svc == "RUNNING")
                Add("Sensor driver", "ok", $"PawnIO {DriverSetup.PawnIoVersion() ?? ""} running".Replace("  ", " "));
            else if (ok)
                Add("Sensor driver", "warn", $"PawnIO installed but service is {svc} — a restart may be needed");
            else
                Add("Sensor driver", "error", "PawnIO missing — per-core, fan and DIMM sensors limited (Security page → Install/Repair)");
        }
        catch (Exception ex) { Add("Sensor driver", "warn", "check failed: " + ex.Message); }

        // 3. Graphics driver
        status?.Invoke("Checking graphics driver…");
        try
        {
            using var q = new ManagementObjectSearcher(
                "SELECT Name, ConfigManagerErrorCode, DriverVersion FROM Win32_VideoController");
            int gpus = 0;
            foreach (ManagementObject o in q.Get())
            {
                gpus++;
                string name = o["Name"]?.ToString()?.Trim() ?? "Display adapter";
                int code    = Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0);
                string ver  = o["DriverVersion"]?.ToString() ?? "";
                if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase))
                    Add("Graphics driver", "error", $"{name} in use — install your NVIDIA / AMD / Intel driver");
                else if (code != 0 && code != 22)
                    Add("Graphics driver", "error", $"{name}: {Describe(code)}");
                else
                    Add("Graphics driver", "ok", $"{name} {ver}".Trim());
            }
            if (gpus == 0) Add("Graphics driver", "warn", "no display adapter reported by Windows");
        }
        catch (Exception ex) { Add("Graphics driver", "warn", "check failed: " + ex.Message); }

        // 4. Every other device with a driver problem (what Device Manager shows with a ⚠)
        status?.Invoke("Checking other device drivers…");
        try
        {
            using var q = new ManagementObjectSearcher(
                "SELECT Name, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
            var bad = new List<string>();
            foreach (ManagementObject o in q.Get())
            {
                int code = Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0);
                if (code is 0 or 22 or 45) continue;
                if (string.Equals(o["PNPClass"]?.ToString(), "Display", StringComparison.OrdinalIgnoreCase)) continue; // covered above
                string name = o["Name"]?.ToString()?.Trim() is { Length: > 0 } n ? n : "Unknown device";
                bad.Add($"{name} ({Describe(code)})");
            }
            if (bad.Count == 0) Add("Device drivers", "ok", "no problems in Device Manager");
            else foreach (var b in bad.Take(8)) Add("Device driver", "warn", b);
            if (bad.Count > 8) Add("Device drivers", "warn", $"…and {bad.Count - 8} more — see Device Manager");
        }
        catch (Exception ex) { Add("Device drivers", "warn", "check failed: " + ex.Message); }

        // 5. Optional components shipped next to Vexis.exe
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "hidapi.dll")))
            Add("MSI RGB library", "warn", "hidapi.dll missing — MSI motherboard RGB unavailable (reinstall Vexis)");

        Results = results;
        RanAt   = DateTime.Now;
        Console.WriteLine($"[check] Done — {Problems} problem(s).");
        return results;
    }

    private static string Describe(int code) =>
        Codes.TryGetValue(code, out var d) ? $"code {code}, {d}" : $"code {code}";
}
