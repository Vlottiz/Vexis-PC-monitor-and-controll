using System.Diagnostics;

/// <summary>
/// Finding, starting and stopping OpenRGB. Vexis talks to it through its SDK server
/// (127.0.0.1:6742), so it is always started with --server; its window can still be opened.
/// </summary>
public static class OpenRgbLauncher
{
    // Vexis runs as admin, so it only starts OpenRGB from folders a normal user can't write
    // to (Program Files). A copy in AppData, C:\OpenRGB or on PATH could be swapped for
    // anything by any program, and would then run elevated. Those copies have to be
    // started by hand (with "--server"); Vexis still connects to them.
    private static IEnumerable<string> Candidates()
    {
        foreach (var pf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string root = Environment.GetFolderPath(pf);
            if (string.IsNullOrEmpty(root)) continue;
            yield return Path.Combine(root, "OpenRGB", "OpenRGB.exe");
            yield return Path.Combine(root, "Vexis", "OpenRGB.exe");
        }
    }

    public static string? FindExe() => Candidates().FirstOrDefault(File.Exists);

    public static bool IsRunning() => Process.GetProcessesByName("OpenRGB").Length > 0;

    public static void StopAll()
    {
        foreach (var p in Process.GetProcessesByName("OpenRGB"))
            try { p.Kill(); Console.WriteLine($"[rgb] Stopped OpenRGB (PID {p.Id})"); } catch (Exception ex) { Console.WriteLine($"[rgb] Couldn't stop OpenRGB (PID {p.Id}): {ex.Message}"); }
    }

    /// <summary>Starts OpenRGB from Program Files with its SDK server on; otherwise opens the download page.</summary>
    public static void Start()
    {
        string? exe = FindExe();
        if (exe == null)
        {
            Console.WriteLine("[rgb] OpenRGB isn't installed in Program Files — opening the download page. " +
                              "A portable copy elsewhere has to be started by hand with --server.");
            Process.Start(new ProcessStartInfo { FileName = "https://openrgb.org", UseShellExecute = true });
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = exe, Arguments = "--server", UseShellExecute = true });
            Console.WriteLine($"[rgb] Started OpenRGB --server from {exe}");
        }
        catch (Exception ex) { Console.WriteLine($"[rgb] Couldn't start OpenRGB: {ex.Message}"); }
    }
}
