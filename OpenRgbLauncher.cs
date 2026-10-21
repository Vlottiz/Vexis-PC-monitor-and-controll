using System.Diagnostics;

/// <summary>
/// Finding, starting and stopping OpenRGB. Vexis talks to it through its SDK server
/// (127.0.0.1:6742), so it is always started with --server; its window can still be opened.
/// </summary>
public static class OpenRgbLauncher
{
    // Vexis runs as admin, so it only starts OpenRGB from folders a normal user can't write
    // to: anywhere under Program Files. A copy in AppData, C:\OpenRGB or on PATH could be
    // swapped (or have a DLL planted next to it) by any program, and would then run elevated.
    // Those copies are found and reported, but have to be started by hand (with "--server");
    // Vexis still connects to them.
    internal static IEnumerable<string> Candidates()
    {
        foreach (var pf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string root = Environment.GetFolderPath(pf);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            yield return Path.Combine(root, "Vexis", "OpenRGB.exe");
            // "OpenRGB", or the zip's own folder name "OpenRGB Windows 64-bit", one or two levels down
            foreach (var dir in SafeDirs(root, "OpenRGB*"))
            {
                yield return Path.Combine(dir, "OpenRGB.exe");
                foreach (var sub in SafeDirs(dir, "OpenRGB*")) yield return Path.Combine(sub, "OpenRGB.exe");
            }
        }
    }

    // Copies in folders the user can write to: found only to tell the user where they are
    private static IEnumerable<string> UnsafeCandidates()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var dir in new[] { Path.Combine(local, "OpenRGB"), Path.Combine(local, "Programs", "OpenRGB"),
                                    @"C:\OpenRGB", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") })
        {
            yield return Path.Combine(dir, "OpenRGB.exe");
            foreach (var sub in SafeDirs(dir, "OpenRGB*")) yield return Path.Combine(sub, "OpenRGB.exe");
        }
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(p.Trim(), "OpenRGB.exe");
    }

    private static IEnumerable<string> SafeDirs(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir, pattern) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }   // no access: skip
    }

    public static string? FindExe() => Candidates().FirstOrDefault(File.Exists);
    public static string? FindUnsafeExe() { try { return UnsafeCandidates().FirstOrDefault(File.Exists); } catch { return null; } }

    public static bool IsRunning() => Process.GetProcessesByName("OpenRGB").Length > 0;

    public static void StopAll()
    {
        foreach (var p in Process.GetProcessesByName("OpenRGB"))
            try { p.Kill(); Console.WriteLine($"[rgb] Stopped OpenRGB (PID {p.Id})"); } catch (Exception ex) { Console.WriteLine($"[rgb] Couldn't stop OpenRGB (PID {p.Id}): {ex.Message}"); }
    }

    /// <summary>
    /// Starts OpenRGB from Program Files with its SDK server on. If it isn't there, says why
    /// (and where a copy was found, if one was) instead of starting anything.
    /// </summary>
    public static (bool ok, string message) Start()
    {
        string? exe = FindExe();
        if (exe == null)
        {
            string? other = FindUnsafeExe();
            string msg = other != null
                ? $"OpenRGB is at {other}. Vexis runs as administrator, so it only starts OpenRGB from Program Files " +
                  "(other programs can change files in that folder). Move the OpenRGB folder into C:\\Program Files\\, " +
                  "or start OpenRGB yourself with --server (Vexis connects to it either way)."
                : "OpenRGB wasn't found in Program Files. Install it there (the folder can be called OpenRGB or " +
                  "\"OpenRGB Windows 64-bit\"), or start it yourself with --server. Opening the download page.";
            Console.WriteLine("[rgb] " + msg);
            if (other == null) SysTools.OpenUrl("https://openrgb.org");
            return (false, msg);
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = exe, Arguments = "--server", UseShellExecute = true,
                                                 WorkingDirectory = Path.GetDirectoryName(exe)! });
            Console.WriteLine($"[rgb] Started OpenRGB --server from {exe}");
            return (true, "");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[rgb] Couldn't start OpenRGB: {ex.Message}");
            return (false, "Couldn't start OpenRGB: " + ex.Message);
        }
    }
}
