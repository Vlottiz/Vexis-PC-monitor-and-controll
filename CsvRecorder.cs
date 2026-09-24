using System.Globalization;
using System.Text;

/// <summary>State of the CSV recorder, sent to the pages with every reading.</summary>
public class RecordStatus
{
    public bool   active  { get; set; }
    public string file    { get; set; } = "";
    public int    rows    { get; set; }
    public int    seconds { get; set; }
}

/// <summary>
/// Writes every sensor reading to a CSV file (Documents\Vexis Logs) while recording.
/// Each row is flushed straight away, so the file is complete up to the last
/// reading even if the PC crashes or loses power — useful for finding out what
/// the hardware was doing just before.
/// Columns are fixed when recording starts (cores and fans present at that moment).
/// </summary>
public sealed class CsvRecorder : IDisposable
{
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string   _path = "";
    private DateTime _started;
    private int      _rows;
    private List<int>    _coreIds  = new();
    private List<string> _fanNames = new();

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
                    rows    = _rows,
                    seconds = _writer != null ? (int)(DateTime.Now - _started).TotalSeconds : 0
                };
        }
    }

    // (header, value) pairs for the fixed columns
    private static readonly (string Name, Func<SensorData, float?> Get)[] Columns =
    {
        ("CPU Temp (C)",        d => d.cpu_temp),
        ("CCD0 Temp (C)",       d => d.ccd0_temp),
        ("CCD1 Temp (C)",       d => d.ccd1_temp),
        ("CPU Power (W)",       d => d.pkg_power),
        ("CPU Load (%)",        d => d.cpu_load),
        ("CPU Avg Clock (MHz)", d => d.avg_clk),
        ("RAM Load (%)",        d => d.ram_load),
        ("RAM Used (GB)",       d => d.ram_used_gb),
        ("GPU Temp (C)",        d => d.gpu?.temp_core ?? d.gpu_temp),
        ("GPU Hot Spot (C)",    d => d.gpu?.temp_hotspot),
        ("GPU Memory Temp (C)", d => d.gpu?.temp_mem),
        ("GPU Load (%)",        d => d.gpu?.load_core ?? d.gpu_load),
        ("GPU Clock (MHz)",     d => d.gpu?.clk_core ?? d.gpu_clock),
        ("GPU Memory Clock (MHz)", d => d.gpu?.clk_mem),
        ("GPU Power (W)",       d => d.gpu?.power ?? d.gpu_power),
        ("GPU Voltage (V)",     d => d.gpu?.voltage ?? d.gpu_voltage),
        ("VRAM Used (MB)",      d => d.gpu?.vram_used_mb),
        ("GPU Fan (RPM)",       d => d.gpu?.fan_rpm),
        ("FPS",                 d => d.gpu?.fps),
    };

    public string Start(SensorData first)
    {
        lock (_lock)
        {
            if (_writer != null) return _path;
            Directory.CreateDirectory(Folder);
            _path     = Path.Combine(Folder, $"vexis-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");
            _coreIds  = first.cores.Keys.OrderBy(k => k).ToList();
            _fanNames = first.fans?.Select(f => f.name).Distinct().ToList() ?? new();
            _writer   = new StreamWriter(_path, false, new UTF8Encoding(true)) { AutoFlush = true };
            _started  = DateTime.Now;
            _rows     = 0;

            var head = new List<string> { "Time", "Elapsed (s)" };
            head.AddRange(Columns.Select(c => c.Name));
            head.AddRange(_coreIds.Select(id => $"Core {id} (MHz)"));
            head.AddRange(_fanNames.Select(n => $"{n} (RPM)"));
            _writer.WriteLine(string.Join(",", head.Select(Quote)));
            Console.WriteLine($"[record] Recording to {_path}");
            return _path;
        }
    }

    public void Append(SensorData d)
    {
        lock (_lock)
        {
            if (_writer == null) return;
            try
            {
                var inv = CultureInfo.InvariantCulture;
                string F(float? v) => v is float f && !float.IsNaN(f) ? f.ToString("0.##", inv) : "";
                var row = new List<string>
                {
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", inv),
                    (DateTime.Now - _started).TotalSeconds.ToString("0.0", inv)
                };
                row.AddRange(Columns.Select(c => F(c.Get(d))));
                row.AddRange(_coreIds.Select(id => F(d.cores.TryGetValue(id, out var c) ? c.clk : null)));
                row.AddRange(_fanNames.Select(n => F(d.fans?.FirstOrDefault(f => f.name == n)?.rpm)));
                _writer.WriteLine(string.Join(",", row));
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
        try { _writer.Dispose(); } catch { }
        _writer = null;
        LastFile = _path;
        Console.WriteLine($"[record] Stopped — {_rows} rows in {_path}");
        return _path;
    }

    private static string Quote(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public void Dispose() => Stop();
}
