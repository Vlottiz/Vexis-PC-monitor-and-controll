using System.Diagnostics;
using System.Text;

/// <summary>
/// Notices when Vexis didn't close properly and helps report it.
///
/// While Vexis runs, %AppData%\Vexis\running.flag exists; a clean exit deletes it.
/// If it is still there at the next launch, the previous session ended abnormally
/// (crash, forced close, power loss). Unhandled errors are also written to
/// crash.txt. The previous debug log is kept as debug-previous.log (the normal log
/// is overwritten every launch), and the app shows a banner offering to open the
/// log or report the problem on GitHub with the details filled in.
/// </summary>
public static class CrashHelper
{
    private static string Dir      => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vexis");
    private static string FlagPath => Path.Combine(Dir, "running.flag");
    private static string CrashPath => Path.Combine(Dir, "crash.txt");
    public  static string LogPath  => Path.Combine(Dir, "debug.log");
    public  static string PreviousLogPath => Path.Combine(Dir, "debug-previous.log");

    public record Report(string when, string summary, bool hasError);
    public static Report? Pending { get; private set; }

    /// <summary>
    /// Call first thing, before the log is opened (it is truncated on open).
    /// <paramref name="replacedOtherInstance"/>: this launch closed a running Vexis,
    /// so a leftover flag is ours, not a crash.
    /// </summary>
    public static void OnStart(bool replacedOtherInstance)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            bool flagLeft = File.Exists(FlagPath) && !replacedOtherInstance;
            bool hasCrash = File.Exists(CrashPath);
            if (flagLeft || hasCrash)
            {
                if (File.Exists(LogPath)) File.Copy(LogPath, PreviousLogPath, overwrite: true);
                string when = File.Exists(FlagPath) ? File.GetLastWriteTime(LogPath).ToString("yyyy-MM-dd HH:mm") : "";
                string summary = hasCrash
                    ? File.ReadAllText(CrashPath)
                    : "Vexis didn't close properly. No error was recorded, which usually means it was force-closed, " +
                      "the PC lost power or restarted, or Windows itself crashed.";
                Pending = new Report(when, summary.Length > 4000 ? summary[..4000] + "…" : summary, hasCrash);
            }
            File.WriteAllText(FlagPath, $"{Environment.ProcessId} {DateTime.Now:O}");
        }
        catch { }
    }

    /// <summary>Clean exit: remove the marker.</summary>
    public static void OnCleanExit()
    {
        try { File.Delete(FlagPath); } catch { }
    }

    /// <summary>Hooks global handlers so unexpected errors are written down.</summary>
    public static void InstallHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Record("Unhandled error", e.ExceptionObject as Exception);
        Application.ThreadException += (_, e) =>
        {
            // UI-thread errors are recorded but Vexis keeps running
            Record("Error on the UI thread", e.Exception, fatal: false);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.WriteLine($"[error] Background task: {e.Exception.GetBaseException().Message}");
            e.SetObserved();
        };
    }

    public static void Record(string what, Exception? ex, bool fatal = true)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{what} — Vexis {MainForm.AppVersionText} — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(ex?.ToString() ?? "(no exception details)");
            Console.WriteLine($"[crash] {what}: {ex?.GetBaseException().Message}");
            if (fatal) File.WriteAllText(CrashPath, sb.ToString());
        }
        catch { }
    }

    // ── Actions from the banner ──────────────────────────────────────────────
    public static void OpenLog()
    {
        string file = File.Exists(PreviousLogPath) ? PreviousLogPath : LogPath;
        try { Process.Start("explorer.exe", $"/select,\"{file}\""); } catch { }
    }

    /// <summary>Opens a new GitHub issue with version, system and error details filled in.</summary>
    public static void OpenReport(HwInfo? info)
    {
        var r = Pending;
        string title = r?.hasError == true ? "Crash: " + FirstLine(r.summary) : "Vexis closed unexpectedly";
        var body = new StringBuilder();
        body.AppendLine("**What were you doing when it happened?**");
        body.AppendLine();
        body.AppendLine("<!-- e.g. opened the Fan Control page, was gaming, PC went to sleep -->");
        body.AppendLine();
        body.AppendLine("**System**");
        body.AppendLine($"- Vexis {MainForm.AppVersionText}");
        body.AppendLine($"- {Environment.OSVersion.VersionString}");
        if (info != null)
        {
            if (!string.IsNullOrEmpty(info.cpu)) body.AppendLine($"- CPU: {info.cpu}");
            if (!string.IsNullOrEmpty(info.gpu)) body.AppendLine($"- GPU: {info.gpu}");
        }
        body.AppendLine();
        body.AppendLine("**Details**");
        body.AppendLine("```");
        body.AppendLine(r?.summary.Length > 1500 ? r.summary[..1500] + "…" : r?.summary ?? "");
        body.AppendLine("```");
        body.AppendLine();
        body.AppendLine("Please drag `debug-previous.log` from `%AppData%\\Vexis` into this issue (the Open log button shows it).");
        string url = $"https://github.com/{UpdateManager.Repo}/issues/new?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body.ToString())}";
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
    }

    public static void Dismiss()
    {
        Pending = null;
        try { File.Delete(CrashPath); } catch { }
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n').Skip(1).FirstOrDefault()?.Trim() ?? s;
        return line.Length > 90 ? line[..90] : line;
    }
}
