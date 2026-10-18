using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;

/// <summary>
/// In-memory ring buffer for live log display in the Security tab.
/// Tees Console.WriteLine output to both the log file and this buffer.
/// The page polls with the last sequence number it saw ("getLogs") and gets only newer lines,
/// so it never has to re-download the whole log.
/// </summary>
public static class AppLog
{
    private static readonly Queue<string> _lines = new();
    private static          int           _seq   = 0;     // total lines ever added (keeps counting past MAX)
    private static readonly object        _lock  = new();
    private const           int           MAX    = 600;   // lines kept; older ones drop off the front

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
            // Otherwise send only the new tail: (_seq - afterSeq) lines arrived since then
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

    // debug.log stops growing at this size (days of uptime with RGB effects could otherwise
    // fill gigabytes). The live view in the Security tab keeps going.
    private const long MaxFileBytes = 20 * 1024 * 1024;
    private long _written;
    private bool _full;

    public override void WriteLine(string? value)
    {
        string ts  = DateTime.Now.ToString("HH:mm:ss.fff");
        string line = $"[{ts}] {value ?? ""}";
        lock (this) WriteFile(line + Environment.NewLine);
        AppLog.Append(line);
    }

    private void WriteFile(string text)
    {
        if (_full) return;
        _written += text.Length;
        if (_written > MaxFileBytes)
        {
            _full = true;
            _file.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [log] debug.log reached {MaxFileBytes / 1048576} MB — not writing more until Vexis restarts");
            return;
        }
        _file.Write(text);
    }

    // Partial writes (no newline) go to the file only: the live view works line by line
    public override void Write(string? value)          { if (value != null) lock (this) WriteFile(value); }
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
        // app.manifest already asks Windows for admin, so the installed .exe normally starts
        // elevated. This catches `dotnet run` from a normal terminal and any launch that
        // skipped the manifest.
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
        // Only one copy may drive the fan chip and the OpenRGB connection; two would fight
        // over every fan speed. The newest launch wins.
        var current = Process.GetCurrentProcess();
        bool replacedOther = false;
        // After "Reset to factory settings": give the old copy time to hand the fans back and close
        int resetIdx = Array.FindIndex(args, a => a.Equals("--after-reset", StringComparison.OrdinalIgnoreCase));
        if (resetIdx >= 0 && resetIdx + 1 < args.Length && int.TryParse(args[resetIdx + 1], out int oldPid))
        {
            try { using var old = Process.GetProcessById(oldPid); old.WaitForExit(10000); } catch { }   // already gone
        }
        // Ask the running copy to close by itself first (the same exit.request the installer
        // uses), so it hands the fans back to the BIOS. Only if it hasn't gone after 5 s is it
        // killed — a killed copy leaves the fans at whatever speed it last set.
        var others = Process.GetProcessesByName(current.ProcessName).Where(p => p.Id != current.Id).ToList();
        if (others.Count > 0)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vexis");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "exit.request"), "new launch");
            }
            catch { }   // can't write it: fall through to the kill below
            var until = DateTime.Now.AddSeconds(5);
            foreach (var p in others)
                try { p.WaitForExit((int)Math.Max(0, (until - DateTime.Now).TotalMilliseconds)); } catch { }   // already gone
            replacedOther = true;
        }
        foreach (var p in others)
        {
            try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch { }   // already gone, or not ours to stop
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
            // false = start a fresh file each launch (CrashHelper has already copied the last one
            // to debug-previous.log). AutoFlush so the log survives a crash.
            var log    = new StreamWriter(logPath, false) { AutoFlush = true };
            var tee    = new TeeWriter(log);
            // From here on every Console.WriteLine in the app lands in debug.log and the Security tab
            Console.SetOut(tee);
            Console.SetError(tee);
            Console.WriteLine($"[log] Writing to {logPath}");
        }
        catch { }   // no log file: Vexis still runs, just without debug.log

        if (CrashHelper.Pending != null) Console.WriteLine("[crash] Previous session ended unexpectedly — log kept as debug-previous.log");
        // SystemAware: WinForms scales once for the main monitor; WebView2 does its own scaling inside
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
