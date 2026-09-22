using System.Diagnostics;
using Microsoft.Win32;

/// <summary>
/// Hardware sensor driver setup.
///
/// LibreHardwareMonitor 0.9.6+ no longer uses WinRing0. All low-level access
/// (Intel MSR per-core temps/clocks, AMD SMU, SuperIO fan chips, SMBus DIMM temps)
/// goes through PawnIO — a signed driver that is compatible with Memory Integrity,
/// VBS and the Vulnerable Driver Blocklist. So Vexis never needs to weaken Windows
/// security; it only needs PawnIO installed once.
///
/// PawnIO: https://pawnio.eu — installer is redistributable unmodified.
/// </summary>
public static class DriverSetup
{
    private const string PawnIoUninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
    private const string PawnIoSetupName = "PawnIO_setup.exe";

    public static bool IsPawnIoInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PawnIoUninstallKey);
            return key != null;
        }
        catch { return false; }
    }

    public static string? PawnIoVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PawnIoUninstallKey);
            return key?.GetValue("DisplayVersion")?.ToString();
        }
        catch { return null; }
    }

    public static bool IsPawnIoServicePresent()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key != null;
        }
        catch { return false; }
    }

    /// <summary>"RUNNING", "STOPPED", ... or "MISSING" — from sc.exe query.</summary>
    public static string PawnIoServiceState()
    {
        if (!IsPawnIoServicePresent()) return "MISSING";
        string output = RunHidden("sc.exe", "query PawnIO");
        foreach (var state in new[] { "RUNNING", "STOPPED", "START_PENDING", "STOP_PENDING" })
            if (output.Contains(state)) return state;
        return "UNKNOWN";
    }

    /// <summary>
    /// Makes sure the PawnIO driver is installed and running, using the copy of
    /// PawnIO_setup.exe shipped next to Vexis.exe.
    ///
    /// Older Vexis builds ran "sc delete PawnIO" on every launch. That removed the
    /// driver service but left PawnIO's uninstall registry entry behind, so PawnIO
    /// looks installed while nothing is actually loaded. That case is repaired by
    /// uninstalling and reinstalling. <paramref name="forceRepair"/> does the same
    /// on demand (Security page button).
    /// </summary>
    public static bool EnsurePawnIo(bool forceRepair = false)
    {
        bool registered = IsPawnIoInstalled();
        bool service    = IsPawnIoServicePresent();

        if (registered && service && !forceRepair)
        {
            if (PawnIoServiceState() != "RUNNING") RunHidden("sc.exe", "start PawnIO");
            Console.WriteLine($"[pawnio] Installed (v{PawnIoVersion() ?? "?"}), service {PawnIoServiceState()}.");
            return true;
        }

        Console.WriteLine(forceRepair ? "[pawnio] Repair requested."
            : registered ? "[pawnio] Registered but driver service missing — repairing."
                         : "[pawnio] Not installed — installing sensor driver.");

        string setup = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PawnIoSetupName);
        if (!File.Exists(setup))
        {
            Console.WriteLine($"[pawnio] {PawnIoSetupName} not found next to Vexis.exe — reinstall Vexis, " +
                              "or install PawnIO manually from https://pawnio.eu");
            return false;
        }

        if (registered)
            Console.WriteLine($"[pawnio] Uninstall: {RunSetup(setup, "-uninstall -silent")}");
        Console.WriteLine($"[pawnio] Install: {RunSetup(setup, "-install -silent")}");

        bool ok = IsPawnIoInstalled() && IsPawnIoServicePresent();
        if (ok && PawnIoServiceState() != "RUNNING") RunHidden("sc.exe", "start PawnIO");
        Console.WriteLine(ok ? $"[pawnio] Installed OK, service {PawnIoServiceState()}."
                             : "[pawnio] Still not installed — CPU sensors will be limited.");
        return ok;
    }

    private static string RunSetup(string setup, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            { FileName = setup, Arguments = args, UseShellExecute = false, CreateNoWindow = true });
            if (p == null) return "failed to start";
            p.WaitForExit(60_000);
            return $"exit code {p.ExitCode}";
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// Removes leftovers from older Vexis builds: the WinRing0 service and file
    /// (flagged by Defender as HackTool:Win32/Winring0 and blocked by Windows).
    /// </summary>
    public static void CleanupLegacyWinRing0()
    {
        try
        {
            using var svc = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\WinRing0_1_2_0");
            if (svc != null)
            {
                RunHidden("sc.exe", "stop WinRing0_1_2_0");
                RunHidden("sc.exe", "delete WinRing0_1_2_0");
                Console.WriteLine("[hw] Removed legacy WinRing0 service.");
            }
            string sys = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinRing0x64.sys");
            if (File.Exists(sys)) File.Delete(sys);
        }
        catch (Exception ex) { Console.WriteLine($"[hw] WinRing0 cleanup skipped: {ex.Message}"); }
    }

    // ── Windows security status (read-only) + restore ──────────────────────────
    // Older Vexis builds switched these off. Vexis no longer touches them, but the
    // Security page lets users see their state and turn them back on.

    public record SecurityStatus(bool pawnio, string? pawnioVersion, string pawnioService,
                                 bool hvci, bool blocklist, bool vbs);

    public static SecurityStatus GetSecurityStatus()
    {
        int? hvci = ReadDword(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
        int? vdb  = ReadDword(@"SYSTEM\CurrentControlSet\Control\CI\Config", "VulnerableDriverBlocklistEnable");
        int? vbs  = ReadDword(@"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity");
        return new SecurityStatus(
            pawnio:        IsPawnIoInstalled(),
            pawnioVersion: PawnIoVersion(),
            pawnioService: PawnIoServiceState(),
            hvci:          hvci == 1,
            blocklist:     vdb != 0,   // missing value = Windows default (on)
            vbs:           vbs == 1);
    }

    /// <summary>Re-enables Memory Integrity, VBS and the driver blocklist. Needs a reboot.</summary>
    public static void RestoreWindowsSecurity()
    {
        void Set(string path, string name)
        {
            try
            {
                using var k = Registry.LocalMachine.CreateSubKey(path, writable: true);
                k.SetValue(name, 1, RegistryValueKind.DWord);
                Console.WriteLine($"[security] Restored {name}=1");
            }
            catch (Exception ex) { Console.WriteLine($"[security] Could not restore {name}: {ex.Message}"); }
        }
        Set(@"SYSTEM\CurrentControlSet\Control\CI\Config", "VulnerableDriverBlocklistEnable");
        Set(@"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity");
        Set(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
        Console.WriteLine("[security] Windows protections restored — restart Windows to apply.");
    }

    private static int? ReadDword(string path, string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(path);
            return k?.GetValue(name) is int v ? v : null;
        }
        catch { return null; }
    }

    private static string RunHidden(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            if (p == null) return "";
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output;
        }
        catch { return ""; }
    }
}
