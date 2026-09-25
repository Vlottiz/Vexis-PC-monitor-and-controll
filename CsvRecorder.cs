using System.Globalization;
using System.Text;

/// <summary>State of the recorder, sent to the pages with every reading.</summary>
public class RecordStatus
{
    public bool   active  { get; set; }
    public string file    { get; set; } = "";
    public string format  { get; set; } = "csv";
    public int    rows    { get; set; }
    public int    seconds { get; set; }
}

/// <summary>What to record, chosen on the CSV Recording page.</summary>
public class RecordOptions
{
    public List<string>? groups   { get; set; }          // cpu, cores, coreload, ram, gpu, fans
    public string?       format   { get; set; } = "csv";  // "csv" (raw) | "pretty" (aligned text table)
    public float?        interval { get; set; }           // seconds between rows; 0 = every reading
}

/// <summary>
/// Records sensor readings to Documents\Vexis Logs while recording is on.
///
/// Raw CSV: one row per reading, for Excel / Google Sheets / the viewer on the
/// CSV Recording page.
/// Pretty: a .txt table with aligned columns, the header repeated every 40 rows
/// and a MIN / AVG / MAX summary at the end, readable in Notepad.
///
/// Each row is flushed straight away, so the file is complete up to the last
/// reading even if the PC crashes. Columns are fixed when recording starts.
/// </summary>
public sealed class CsvRecorder : IDisposable
{
    // Optional = only recorded when this PC reports it at the start (CCD temps on AMD, hot spot...)
    private sealed record Col(string Group, string Long, string Short, Func<SensorData, float?> Get, string Fmt = "0.##", bool Optional = false);

    public static readonly string[] AllGroups = { "cpu", "cores", "coreload", "ram", "gpu", "fans" };

    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string   _path = "", _format = "csv";
    private DateTime _started, _lastRow = DateTime.MinValue;
    private TimeSpan _interval;
    private int      _rows;
    private List<Col> _cols = new();
    private int[]    _widths = Array.Empty<int>();
    private double[] _min = Array.Empty<double>(), _max = Array.Empty<double>(), _sum = Array.Empty<double>();
    private int[]    _cnt = Array.Empty<int>();

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Vexis Logs");

    public bool   IsRecording { get { lock (_lock) return _writer != null; } }
    public string LastFile    { get; private set; } = "";

    public RecordStatus Status
    {
        get
        {
            lock (_lock)
                return new RecordStatus
                {
                    active  = _writer != null,
                    file    = _writer != null ? Path.GetFileName(_path) : Path.GetFileName(LastFile),
                    format  = _format,
                    rows    = _rows,
                    seconds = _writer != null ? (int)(DateTime.Now - _started).TotalSeconds : 0
                };
        }
    }

    private static IEnumerable<Col> BuildColumns(SensorData first, HashSet<string> groups)
    {
        if (groups.Contains("cpu"))
        {
            yield return new("cpu", "CPU Temp (C)",        "CPU °C",   d => d.cpu_temp, "0.0");
            yield return new("cpu", "CCD0 Temp (C)",       "CCD0 °C",  d => d.ccd0_temp, "0.0", true);
            yield return new("cpu", "CCD1 Temp (C)",       "CCD1 °C",  d => d.ccd1_temp, "0.0", true);
            yield return new("cpu", "CPU Power (W)",       "CPU W",    d => d.pkg_power, "0.0");
            yield return new("cpu", "CPU Load (%)",        "CPU %",    d => d.cpu_load, "0.0");
            yield return new("cpu", "CPU Avg Clock (MHz)", "AVG MHz",  d => d.avg_clk, "0");
        }
        var coreIds = first.cores.Keys.OrderBy(k => k).ToList();
        if (groups.Contains("cores"))
            foreach (var id in coreIds)
                yield return new("cores", $"Core {id} (MHz)", $"C{id} MHz", d => d.cores.TryGetValue(id, out var c) ? c.clk : null, "0");
        if (groups.Contains("coreload"))
            foreach (var id in coreIds)
                yield return new("coreload", $"Core {id} Load (%)", $"C{id} %", d => d.cores.TryGetValue(id, out var c) ? c.load : null, "0");
        if (groups.Contains("ram"))
        {
            yield return new("ram", "RAM Load (%)",  "RAM %",  d => d.ram_load, "0.0");
            yield return new("ram", "RAM Used (GB)", "RAM GB", d => d.ram_used_gb, "0.00");
        }
        if (groups.Contains("gpu"))
        {
            yield return new("gpu", "GPU Temp (C)",           "GPU °C",   d => d.gpu?.temp_core ?? d.gpu_temp, "0.0");
            yield return new("gpu", "GPU Hot Spot (C)",       "HOT °C",   d => d.gpu?.temp_hotspot, "0.0", true);
            yield return new("gpu", "GPU Memory Temp (C)",    "VRAM °C",  d => d.gpu?.temp_mem, "0.0", true);
            yield return new("gpu", "GPU Load (%)",           "GPU %",    d => d.gpu?.load_core ?? d.gpu_load, "0.0");
            yield return new("gpu", "GPU Clock (MHz)",        "GPU MHz",  d => d.gpu?.clk_core ?? d.gpu_clock, "0");
            yield return new("gpu", "GPU Memory Clock (MHz)", "MEM MHz",  d => d.gpu?.clk_mem, "0", true);
            yield return new("gpu", "GPU Power (W)",          "GPU W",    d => d.gpu?.power ?? d.gpu_power, "0.0");
            yield return new("gpu", "GPU Voltage (V)",        "GPU V",    d => d.gpu?.voltage ?? d.gpu_voltage, "0.000");
            yield return new("gpu", "VRAM Used (MB)",         "VRAM MB",  d => d.gpu?.vram_used_mb, "0");
            yield return new("gpu", "FPS",                    "FPS",      d => d.gpu?.fps, "0");
        }
        if (groups.Contains("fans"))
            foreach (var name in first.fans?.Select(f => f.name).Distinct() ?? Enumerable.Empty<string>())
            {
                string n = name;
                string shortName = n.Replace("System Fan", "SYS").Replace("Fan", "").Replace("#", "").Trim();
                yield return new("fans", $"{n} (RPM)", (shortName.Length > 0 ? shortName : "FAN") + " RPM",
                                 d => d.fans?.FirstOrDefault(f => f.name == n)?.rpm, "0");
            }
    }

    public string Start(SensorData first, RecordOptions? opt = null)
    {
        lock (_lock)
        {
            if (_writer != null) return _path;
            var groups = new HashSet<string>(opt?.groups?.Where(AllGroups.Contains) ?? AllGroups);
            if (groups.Count == 0) groups = new HashSet<string>(AllGroups);
            _format   = opt?.format == "pretty" ? "pretty" : "csv";
            _interval = TimeSpan.FromSeconds(Math.Clamp(opt?.interval ?? 0, 0, 3600));
            _cols     = BuildColumns(first, groups).Where(c => !c.Optional || c.Get(first) != null).ToList();

            Directory.CreateDirectory(Folder);
            string ext = _format == "pretty" ? "txt" : "csv";
            _path    = Path.Combine(Folder, $"vexis-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{ext}");
            _writer  = new StreamWriter(_path, false, new UTF8Encoding(true)) { AutoFlush = true };
            _started = DateTime.Now;
            _lastRow = DateTime.MinValue;
            _rows    = 0;
            int n = _cols.Count;
            _min = Enumerable.Repeat(double.MaxValue, n).ToArray();
            _max = Enumerable.Repeat(double.MinValue, n).ToArray();
            _sum = new double[n]; _cnt = new int[n];

            if (_format == "pretty") WritePrettyHeader(first, groups);
            else
            {
                var head = new List<string> { "Time", "Elapsed (s)" };
                head.AddRange(_cols.Select(c => c.Long));
                _writer.WriteLine(string.Join(",", head.Select(Quote)));
            }
            Console.WriteLine($"[record] Recording ({_format}, {string.Join("+", groups)}, every {(_interval.TotalSeconds > 0 ? _interval.TotalSeconds + " s" : "reading")}) to {_path}");
            return _path;
        }
    }

    // ── Pretty text layout ────────────────────────────────────────────────────
    private const int PrettyHeaderEvery = 40;

    private void WritePrettyHeader(SensorData first, HashSet<string> groups)
    {
        var w = _writer!;
        var info = first.info;
        w.WriteLine("VEXIS HARDWARE RECORDING");
        w.WriteLine(new string('=', 60));
        w.WriteLine($"Started   {_started:yyyy-MM-dd HH:mm:ss}");
        if (info != null)
        {
            if (!string.IsNullOrEmpty(info.cpu)) w.WriteLine($"CPU       {info.cpu}");
            if (!string.IsNullOrEmpty(info.gpu)) w.WriteLine($"GPU       {info.gpu}{(info.gpuVram > 0 ? $" ({info.gpuVram} GB)" : "")}");
            if (info.ramGb > 0)                  w.WriteLine($"RAM       {info.ramGb} GB {info.ramType} {(info.ramMhz > 0 ? info.ramMhz + " MT/s" : "")}".TrimEnd());
        }
        w.WriteLine($"Interval  {(_interval.TotalSeconds > 0 ? _interval.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s" : "every reading")}");
        w.WriteLine();
        w.WriteLine("Columns:");
        foreach (var c in _cols) w.WriteLine($"  {c.Short,-10} {c.Long}");
        w.WriteLine();

        _widths = _cols.Select(c => Math.Max(c.Short.Length, 7)).ToArray();
        WritePrettyColumnHeader();
    }

    private void WritePrettyColumnHeader()
    {
        var sb = new StringBuilder();
        sb.Append("TIME    ".PadRight(9)).Append("  ELAPSED");
        for (int i = 0; i < _cols.Count; i++) sb.Append("  ").Append(_cols[i].Short.PadLeft(_widths[i]));
        _writer!.WriteLine(sb.ToString());
        _writer.WriteLine(new string('-', sb.Length));
    }

    public void Append(SensorData d)
    {
        lock (_lock)
        {
            if (_writer == null) return;
            var now = DateTime.Now;
            if (_interval > TimeSpan.Zero && now - _lastRow < _interval) return;
            _lastRow = now;
            try
            {
                var inv = CultureInfo.InvariantCulture;
                var vals = new float?[_cols.Count];
                for (int i = 0; i < _cols.Count; i++)
                {
                    float? v = _cols[i].Get(d);
                    if (v is float f && !float.IsNaN(f))
                    {
                        vals[i] = f;
                        _min[i] = Math.Min(_min[i], f); _max[i] = Math.Max(_max[i], f);
                        _sum[i] += f; _cnt[i]++;
                    }
                }
                double elapsed = (now - _started).TotalSeconds;

                if (_format == "pretty")
                {
                    if (_rows > 0 && _rows % PrettyHeaderEvery == 0) { _writer.WriteLine(); WritePrettyColumnHeader(); }
                    var sb = new StringBuilder();
                    sb.Append(now.ToString("HH:mm:ss", inv).PadRight(9)).Append(elapsed.ToString("0.0", inv).PadLeft(9));
                    for (int i = 0; i < _cols.Count; i++)
                        sb.Append("  ").Append((vals[i] is float f ? f.ToString(_cols[i].Fmt, inv) : "-").PadLeft(_widths[i]));
                    _writer.WriteLine(sb.ToString());
                }
                else
                {
                    var row = new List<string> { now.ToString("yyyy-MM-dd HH:mm:ss.fff", inv), elapsed.ToString("0.0", inv) };
                    row.AddRange(vals.Select(v => v is float f ? f.ToString("0.##", inv) : ""));
                    _writer.WriteLine(string.Join(",", row));
                }
                _rows++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[record] Write failed, stopping: {ex.Message}");
                StopLocked();
            }
        }
    }

    /// <summary>Stops recording and returns the file path ("" if nothing was recording).</summary>
    public string Stop()
    {
        lock (_lock) return StopLocked();
    }

    private string StopLocked()
    {
        if (_writer == null) return "";
        try
        {
            if (_format == "pretty") WritePrettySummary();
            _writer.Dispose();
        }
        catch { }
        _writer = null;
        LastFile = _path;
        Console.WriteLine($"[record] Stopped — {_rows} rows in {_path}");
        return _path;
    }

    private void WritePrettySummary()
    {
        var inv = CultureInfo.InvariantCulture;
        var w = _writer!;
        var dur = DateTime.Now - _started;
        w.WriteLine();
        w.WriteLine(new string('=', 60));
        w.WriteLine($"SUMMARY   {_rows} rows over {(int)dur.TotalHours:00}:{dur.Minutes:00}:{dur.Seconds:00}");
        w.WriteLine(new string('=', 60));
        int lw = Math.Max(10, _cols.Count == 0 ? 10 : _cols.Max(c => c.Long.Length));
        w.WriteLine($"{"".PadRight(lw)}  {"MIN",10}  {"AVG",10}  {"MAX",10}");
        for (int i = 0; i < _cols.Count; i++)
        {
            string F(double v) => _cnt[i] == 0 ? "-" : v.ToString(_cols[i].Fmt, inv);
            w.WriteLine($"{_cols[i].Long.PadRight(lw)}  {F(_min[i]),10}  {F(_cnt[i] > 0 ? _sum[i] / _cnt[i] : 0),10}  {F(_max[i]),10}");
        }
    }

    private static string Quote(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // ── Recordings on disk (for the CSV Recording page) ───────────────────────
    public record FileEntry(string name, long size, string modified);

    public static List<FileEntry> ListFiles()
    {
        if (!Directory.Exists(Folder)) return new();
        return new DirectoryInfo(Folder).EnumerateFiles("vexis-*.*")
            .Where(f => f.Extension is ".csv" or ".txt")
            .OrderByDescending(f => f.LastWriteTime)
            .Take(50)
            .Select(f => new FileEntry(f.Name, f.Length, f.LastWriteTime.ToString("yyyy-MM-dd HH:mm")))
            .ToList();
    }

    /// <summary>Full path of a recording by file name, or null if it isn't one (no paths accepted).</summary>
    public static string? ResolveFile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name)) return null;
        string path = Path.Combine(Folder, name);
        return File.Exists(path) ? path : null;
    }

    public void Dispose() => Stop();
}
