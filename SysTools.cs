using System.Diagnostics;

/// <summary>
/// Runs Windows' own tools (sc.exe, schtasks.exe, explorer.exe) by their full path.
/// Vexis runs as administrator. Started by bare name ("sc.exe"), Windows would also look in
/// the current folder before System32, so a program planted there under that name would run
/// with admin rights. A full path and a System32 working folder rule that out.
/// </summary>
public static class SysTools
{
    public static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);
    public static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>
    /// Runs a System32 tool hidden and returns its exit code and output. The timeout really
    /// applies: output is read in the background, so a tool that hangs can't block Vexis.
    /// </summary>
    public static (int exit, string output) Run(string exe, string args, int timeoutMs = 10_000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = System32(exe), Arguments = args, WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            });
            if (p == null) return (-1, "");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }   // already exited
                return (-1, $"{exe} timed out after {timeoutMs / 1000} s");
            }
            return (p.ExitCode, stdout.Result + stderr.Result);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    /// <summary>
    /// Opens a web link through Explorer, so the browser starts as the normal user. Started
    /// straight from Vexis it would inherit Vexis's administrator rights.
    /// </summary>
    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = Explorer, Arguments = $"\"{url}\"", UseShellExecute = false }); }
        catch (Exception ex) { Console.WriteLine($"[link] Couldn't open {url}: {ex.Message}"); }
    }

    /// <summary>Shows a file selected in Explorer.</summary>
    public static void RevealFile(string path)
    {
        try { Process.Start(new ProcessStartInfo { FileName = Explorer, Arguments = $"/select,\"{path}\"", UseShellExecute = false }); }
        catch (Exception ex) { Console.WriteLine($"[files] Couldn't show {path}: {ex.Message}"); }
    }
}
