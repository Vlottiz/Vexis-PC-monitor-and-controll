using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;

/// <summary>
/// In-memory ring buffer for live log display in the Security tab.
/// Tees Console.WriteLine output to both the log file and this buffer.
/// </summary>
public static class AppLog
{
    private static readonly Queue<string> _lines = new();
    private static          int           _seq   = 0;
    private static readonly object        _lock  = new();
    private const           int           MAX    = 600;

    public static void Append(string line)
    {
        lock (_lock)
        {
            _lines.Enqueue(line);
            while (_lines.Count > MAX) _lines.Dequeue();
            _seq++;
        }
    }

    public static (string[] Lines, int Seq) GetAll()
    {
        lock (_lock) { return (_lines.ToArray(), _seq); }
    }

    public static (string[] Lines, int Seq) GetSince(int afterSeq)
    {
        lock (_lock)
        {
            // If more than MAX lines have gone by since last poll, just send all
            if (_seq - afterSeq >= MAX) return (_lines.ToArray(), _seq);
            // Otherwise send only the new tail
            int skip = _lines.Count - (_seq - afterSeq);
            var arr  = skip > 0 ? _lines.Skip(skip).ToArray() : _lines.ToArray();
            return (arr, _seq);
        }
    }
}

/// <summary>Writes to two TextWriters simultaneously — file log + AppLog ring buffer.</summary>
internal sealed class TeeWriter : TextWriter
{
    private readonly TextWriter _file;
    public TeeWriter(TextWriter file) { _file = file; }
    public override System.Text.Encoding Encoding => _file.Encoding;

    public override void WriteLine(string? value)
    {
        string ts  = DateTime.Now.ToString("HH:mm:ss.fff");
        string line = $"[{ts}] {value ?? ""}";
        _file.WriteLine(line);
        AppLog.Append(line);
    }

    public override void Write(string? value)          { _file.Write(value); }
    public override void Flush()                       { _file.Flush(); }
    protected override void Dispose(bool d) { if (d) _file.Dispose(); base.Dispose(d); }
}

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // --minimized: started by the "Start with Windows" logon task — go straight to the tray
        bool startMinimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        // ── Admin check ───────────────────────────────────────────────────────
        if (!IsAdministrator())
        {
            string proc = Process.GetCurrentProcess().ProcessName;

            if (proc.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "Vexis requires Administrator privileges.\n\n" +
                    "Please open an Administrator PowerShell and run:\n  dotnet run",
                    "Vexis — Admin Required",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Running as the real .exe — trigger UAC relaunch
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = Process.GetCurrentProcess().MainModule!.FileName,
                    Arguments       = startMinimized ? "--minimized" : "",
                    UseShellExecute = true,
                    Verb            = "runas"
                });
            }
            catch { /* User cancelled UAC */ }
            return;
        }

        // ── Kill any existing Vexis instances ────────────────────────────
        var current = Process.GetCurrentProcess();
        bool replacedOther = false;
        // After "Reset to factory settings": give the old copy time to hand the fans back and close
        int resetIdx = Array.FindIndex(args, a => a.Equals("--after-reset", StringComparison.OrdinalIgnoreCase));
        if (resetIdx >= 0 && resetIdx + 1 < args.Length && int.TryParse(args[resetIdx + 1], out int oldPid))
        {
            try { using var old = Process.GetProcessById(oldPid); old.WaitForExit(10000); } catch { }
        }
        foreach (var p in Process.GetProcessesByName(current.ProcessName))
        {
            if (p.Id == current.Id) continue;
            try { p.Kill(); p.WaitForExit(3000); replacedOther = true; } catch { }
        }

        // ── Did the last session end badly? (must run before the log is truncated) ──
        CrashHelper.OnStart(replacedOther);
        CrashHelper.InstallHandlers();
        Application.ApplicationExit += (_, _) => CrashHelper.OnCleanExit();

        // ── Log to AppData (always writable, even from Program Files) ──────────
        try
        {
            var logDir  = Path.Combine(Environment.GetFolderPath(
                              Environment.SpecialFolder.ApplicationData), "Vexis");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "debug.log");
            var log    = new StreamWriter(logPath, false) { AutoFlush = true };
            var tee    = new TeeWriter(log);
            Console.SetOut(tee);
            Console.SetError(tee);
            Console.WriteLine($"[log] Writing to {logPath}");
        }
        catch { }

        if (CrashHelper.Pending != null) Console.WriteLine("[crash] Previous session ended unexpectedly — log kept as debug-previous.log");
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Loading screen (not when starting quietly to the tray with Windows)
        if (!startMinimized) SplashForm.ShowSplash(MainForm.AppVersionText);
        Application.Run(new MainForm(startMinimized));
    }

    static bool IsAdministrator()
    {
        using var identity  = WindowsIdentity.GetCurrent();
        var       principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
