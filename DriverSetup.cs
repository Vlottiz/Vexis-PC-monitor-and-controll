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
    private const string PawnIoDownloadUrl =
        "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

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

    /// <summary>
    /// Installs PawnIO if it is missing. Uses the copy shipped next to Vexis.exe
    /// (the installer bundles it) and downloads it only as a fallback.
    /// Returns true when PawnIO is installed afterwards.
    /// </summary>
    public static bool EnsurePawnIo()
    {
        if (IsPawnIoInstalled())
        {
            Console.WriteLine($"[pawnio] Installed (v{PawnIoVersion() ?? "?"}).");
            return true;
        }

        Console.WriteLine("[pawnio] Not installed — installing sensor driver...");
        try
        {
            string setup = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PawnIoSetupName);
            if (!File.Exists(setup))
            {
                setup = Path.Combine(Path.GetTempPath(), PawnIoSetupName);
                Console.WriteLine("[pawnio] Bundled installer missing, downloading...");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.Add("User-Agent", "Vexis");
                var bytes = http.GetByteArrayAsync(PawnIoDownloadUrl).GetAwaiter().GetResult();
                File.WriteAllBytes(setup, bytes);
            }

            using var p = Process.Start(new ProcessStartInfo
            {
                FileName        = setup,
                Arguments       = "-install -silent",
                UseShellExecute = false,
                CreateNoWindow  = true
            });
            p?.WaitForExit(60_000);
            Console.WriteLine($"[pawnio] Setup exit code: {p?.ExitCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[pawnio] Install failed: {ex.Message}");
        }

        bool ok = IsPawnIoInstalled();
        Console.WriteLine(ok ? "[pawnio] Installed OK." : "[pawnio] Still not installed — CPU sensors will be limited.");
        return ok;
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

    public record SecurityStatus(bool pawnio, string? pawnioVersion,
                                 bool hvci, bool blocklist, bool vbs);

    public static SecurityStatus GetSecurityStatus()
    {
        int? hvci = ReadDword(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
        int? vdb  = ReadDword(@"SYSTEM\CurrentControlSet\Control\CI\Config", "VulnerableDriverBlocklistEnable");
        int? vbs  = ReadDword(@"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity");
        return new SecurityStatus(
            pawnio:        IsPawnIoInstalled(),
            pawnioVersion: PawnIoVersion(),
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

    private static void RunHidden(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            { FileName = file, Arguments = args, UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(3000);
        }
        catch { }
    }
}
