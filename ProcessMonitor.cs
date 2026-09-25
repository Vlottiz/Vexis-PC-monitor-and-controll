using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Task-Manager-style process list for the Processes page.
///
/// Sampled only while that page is open (it asks every ~1.5 s), so it costs
/// nothing otherwise. CPU % comes from the change in each process's CPU time
/// between two samples; GPU % from Windows' "GPU Engine" performance counters
/// (the same source Task Manager uses: per process, the busiest engine type).
/// Processes are grouped by executable, like Task Manager's Apps / Background
/// processes / Windows processes sections.
/// </summary>
public sealed class ProcessMonitor : IDisposable
{
    public record ProcRow(int pid, string title, double cpu, long mem, double gpu);
    public record Group(string key, string name, string exe, string type, double cpu, long mem, double gpu,
                        int count, bool canEnd, List<ProcRow> procs);
    public record Snapshot(double cpu, long mem, long memTotal, double gpu, int procCount, int threadCount,
                           List<Group> groups, Dictionary<string, string> icons);

    private readonly object _lock = new();
    private Dictionary<int, (TimeSpan cpu, DateTime at)> _prevCpu = new();
    private readonly Dictionary<int, string?> _pathCache = new();          // pid -> exe path
    private readonly Dictionary<string, string> _descCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _iconCache = new(StringComparer.OrdinalIgnoreCase);  // exe name -> data URL
    private readonly HashSet<string> _iconsSent = new(StringComparer.OrdinalIgnoreCase);
    private PerformanceCounterCategory? _gpuCat;
    private Dictionary<string, CounterSample> _prevGpu = new();
    private bool _gpuFailed;
    private static readonly int SelfPid = Environment.ProcessId;

    // Processes Windows can't run without — never ended from here
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "fontdrvhost", "dwm", "Memory Compression", "Secure System", "MsMpEng", "NisSrv",
        "SecurityHealthService", "sihost", "audiodg", "spoolsv", "LsaIso", "WUDFHost", "conhost",
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, a, b, c, d, e; }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);

    // One pass over the top-level windows: pid -> title of its first visible, titled, unowned window
    private static Dictionary<int, string> WindowTitles()
    {
        var map = new Dictionary<int, string>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            int len = GetWindowTextLength(h);
            if (len == 0) return true;
            GetWindowThreadProcessId(h, out int pid);
            if (map.ContainsKey(pid)) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            map[pid] = sb.ToString();
            return true;
        }, IntPtr.Zero);
        return map;
    }

    // Works for protected processes too, unlike Process.MainModule
    private string? ExePath(int pid)
    {
        if (_pathCache.TryGetValue(pid, out var p)) return p;
        string? path = null;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            var sb = new StringBuilder(1024); int size = sb.Capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString(0, size);
            CloseHandle(h);
        }
        return _pathCache[pid] = path;
    }

    private string Describe(string exe, string? path)
    {
        if (_descCache.TryGetValue(exe, out var d)) return d;
        string desc = exe;
        try
        {
            if (path != null)
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(v.FileDescription)) desc = v.FileDescription.Trim();
                else if (!string.IsNullOrWhiteSpace(v.ProductName)) desc = v.ProductName.Trim();
            }
        }
        catch { }
        return _descCache[exe] = desc;
    }

    private string? Icon(string exe, string? path)
    {
        if (_iconCache.TryGetValue(exe, out var url)) return url;
        if (path == null) return null;
        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (ico == null) return null;
            using var bmp = new Bitmap(ico.ToBitmap(), new Size(16, 16));
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return _iconCache[exe] = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }

    private static bool IsWindowsProcess(string exe, string? path, int sessionId) =>
        Critical.Contains(exe) || sessionId == 0 && path == null ||
        path != null && path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
                     && !exe.Equals("explorer", StringComparison.OrdinalIgnoreCase);

    // ── GPU per process (GPU Engine counters: pid_1234_luid_..._engtype_3D) ───
    private Dictionary<int, double> ReadGpu()
    {
        var result = new Dictionary<int, double>();
        if (_gpuFailed) return result;
        try
        {
            _gpuCat ??= new PerformanceCounterCategory("GPU Engine");
            var data = _gpuCat.ReadCategory()["Utilization Percentage"];
            if (data == null) return result;
            var now = new Dictionary<string, CounterSample>();
            var perPidEngine = new Dictionary<(int, string), double>();
            foreach (InstanceData inst in data.Values)
            {
                string name = inst.InstanceName;
                now[name] = inst.Sample;
                if (!_prevGpu.TryGetValue(name, out var prev)) continue;
                int p = name.IndexOf("pid_", StringComparison.Ordinal), e = name.IndexOf("engtype_", StringComparison.Ordinal);
                if (p < 0 || e < 0) continue;
                int end = name.IndexOf('_', p + 4);
                if (end < 0 || !int.TryParse(name.AsSpan(p + 4, end - p - 4), out int pid)) continue;
                double v = CounterSample.Calculate(prev, inst.Sample);
                if (double.IsNaN(v) || v <= 0) continue;
                var key = (pid, name[(e + 8)..]);
                perPidEngine[key] = perPidEngine.GetValueOrDefault(key) + v;
            }
            _prevGpu = now;
            foreach (var ((pid, _), v) in perPidEngine)
                result[pid] = Math.Min(100, Math.Max(result.GetValueOrDefault(pid), v));
        }
        catch (Exception ex)
        {
            _gpuFailed = true;
            Console.WriteLine($"[procs] GPU per-process counters unavailable: {ex.Message}");
        }
        return result;
    }

    public Snapshot Sample()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            int cpus = Environment.ProcessorCount;
            var gpu = ReadGpu();
            var windows = WindowTitles();
            var nextCpu = new Dictionary<int, (TimeSpan, DateTime)>();
            var rows = new List<(string exe, string? path, string type, ProcRow row)>();
            int threads = 0;
            var procs = Process.GetProcesses();
            foreach (var p in procs)
            {
                try
                {
                    int pid = p.Id;
                    string exe = pid == 0 ? "Idle" : p.ProcessName;
                    if (pid == 0) continue;                       // Task Manager hides the idle process too
                    string? path = ExePath(pid);
                    double cpu = 0;
                    try
                    {
                        var t = p.TotalProcessorTime;
                        nextCpu[pid] = (t, now);
                        if (_prevCpu.TryGetValue(pid, out var prev) && now > prev.at)
                            cpu = Math.Max(0, (t - prev.cpu).TotalMilliseconds / ((now - prev.at).TotalMilliseconds * cpus) * 100);
                    }
                    catch { }                                     // protected process: no CPU time
                    long mem = 0; try { mem = p.WorkingSet64; } catch { }
                    try { threads += p.Threads.Count; } catch { }
                    string title = windows.GetValueOrDefault(pid, "");
                    int session = 0; try { session = p.SessionId; } catch { }
                    string type = title.Length > 0 ? "app"
                                : IsWindowsProcess(exe, path, session) ? "win" : "bg";
                    rows.Add((exe, path, type, new ProcRow(pid, title, Math.Round(Math.Min(cpu, 100), 1), mem, Math.Round(gpu.GetValueOrDefault(pid), 1))));
                }
                catch { }
                finally { p.Dispose(); }
            }
            _prevCpu = nextCpu;
            foreach (var dead in _pathCache.Keys.Where(k => !nextCpu.ContainsKey(k)).ToList()) _pathCache.Remove(dead);

            var icons = new Dictionary<string, string>();
            var groups = rows.GroupBy(r => r.exe, StringComparer.OrdinalIgnoreCase).Select(g =>
            {
                string exe = g.Key;
                string? path = g.Select(r => r.path).FirstOrDefault(x => x != null);
                // A group counts as an app if any of its processes has a window
                string type = g.Any(r => r.type == "app") ? "app" : g.Any(r => r.type == "win") ? "win" : "bg";
                if (!_iconsSent.Contains(exe) && Icon(exe, path) is string url) { icons[exe] = url; _iconsSent.Add(exe); }
                var list = g.Select(r => r.row).OrderByDescending(r => r.cpu).ThenByDescending(r => r.mem).ToList();
                bool canEnd = !Critical.Contains(exe) && list.All(r => r.pid > 4 && r.pid != SelfPid);
                string name = list.FirstOrDefault(r => r.title.Length > 0)?.title is { Length: > 0 } t && type == "app" && list.Count == 1
                    ? t : Describe(exe, path);
                return new Group(exe, name, exe + ".exe", type,
                    Math.Round(list.Sum(r => r.cpu), 1), list.Sum(r => r.mem), Math.Round(Math.Min(100, list.Sum(r => r.gpu)), 1),
                    list.Count, canEnd, list);
            }).ToList();

            var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            GlobalMemoryStatusEx(ref ms);
            return new Snapshot(
                Math.Round(Math.Min(100, groups.Sum(g => g.cpu)), 1),
                (long)(ms.ullTotalPhys - ms.ullAvailPhys), (long)ms.ullTotalPhys,
                Math.Round(Math.Min(100, gpu.Values.DefaultIfEmpty(0).Max()), 1),
                rows.Count, threads, groups, icons);
        }
    }

    /// <summary>The page reloaded: send every icon again next time.</summary>
    public void ResetIcons() { lock (_lock) _iconsSent.Clear(); }

    /// <summary>Ends one process, or every process of a group (by exe name).</summary>
    public (bool ok, string message) End(int? pid, string? exe)
    {
        var targets = new List<Process>();
        try
        {
            if (pid is int id) targets.Add(Process.GetProcessById(id));
            else if (!string.IsNullOrEmpty(exe)) targets.AddRange(Process.GetProcessesByName(exe));
            if (targets.Count == 0) return (false, "Process not found — it may have already closed.");
            foreach (var p in targets)
            {
                if (p.Id <= 4 || p.Id == SelfPid || Critical.Contains(p.ProcessName))
                    return (false, $"{p.ProcessName} is a Windows process that can't be ended safely.");
            }
            int ended = 0; string? error = null;
            foreach (var p in targets)
            {
                try { p.Kill(); ended++; }
                catch (Exception ex) { error = ex.Message; }
            }
            string what = targets[0].ProcessName;
            Console.WriteLine($"[procs] End task {what}: {ended}/{targets.Count} ended{(error != null ? " — " + error : "")}");
            return ended > 0 ? (true, $"Ended {what}{(ended > 1 ? $" ({ended} processes)" : "")}.")
                             : (false, $"Couldn't end {what}: {error}");
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { foreach (var p in targets) p.Dispose(); }
    }

    public void Dispose() { }
}
