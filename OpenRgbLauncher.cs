using System.Diagnostics;

/// <summary>
/// Finding, starting and stopping OpenRGB. Vexis talks to it through its SDK server
/// (127.0.0.1:6742), so it is always started with --server; its window can still be opened.
/// </summary>
public static class OpenRgbLauncher
{
    // Where OpenRGB usually ends up: next to Vexis, the installer, a portable copy
    private static IEnumerable<string> Candidates()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(AppContext.BaseDirectory, "OpenRGB.exe");
        yield return @"C:\Program Files\OpenRGB\OpenRGB.exe";
        yield return @"C:\Program Files (x86)\OpenRGB\OpenRGB.exe";
        yield return Path.Combine(local, "OpenRGB", "OpenRGB.exe");
        yield return Path.Combine(local, "Programs", "OpenRGB", "OpenRGB.exe");
        yield return @"C:\OpenRGB\OpenRGB.exe";
        yield return @"C:\Tools\OpenRGB\OpenRGB.exe";
    }

    public static string? FindExe() => Candidates().FirstOrDefault(File.Exists);

    public static bool IsRunning() => Process.GetProcessesByName("OpenRGB").Length > 0;

    public static void StopAll()
    {
        foreach (var p in Process.GetProcessesByName("OpenRGB"))
            try { p.Kill(); Console.WriteLine($"[rgb] Stopped OpenRGB (PID {p.Id})"); } catch { }
    }

    /// <summary>Starts OpenRGB with its SDK server on. Falls back to PATH, then to the download page.</summary>
    public static void Start()
    {
        string? exe = FindExe();
        try
        {
            Process.Start(new ProcessStartInfo { FileName = exe ?? "OpenRGB.exe", Arguments = "--server", UseShellExecute = true });
            Console.WriteLine($"[rgb] Started OpenRGB --server from {exe ?? "PATH"}");
        }
        catch
        {
            Console.WriteLine("[rgb] OpenRGB not found — opening the download page");
            Process.Start(new ProcessStartInfo { FileName = "https://openrgb.org", UseShellExecute = true });
        }
    }
}
