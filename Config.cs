// V was here ✌  As of 2.13.0, 80% of the code in this file is still my original work (Vlottiz). The rest came later, some of it AI-assisted (see CONTRIBUTING.md). Don't take my word for it: git blame.
using System.Text.Json;
using System.Text.Json.Serialization;

// ─── Fan curve data ────────────────────────────────────────────────────────────
// ✌ V was here — 93% of this section is still my original code
// Everything in this file is saved as %AppData%\Vexis\config.json. Property names are the
// JSON keys, so renaming one loses that setting for existing users.
public class FanCurvePoint
{
    public int Temp  { get; set; }  // °C
    public int Speed { get; set; }  // 0–100%
}

public class FanCurve
{
    public string           FanName    { get; set; } = "";
    public string           TempSource { get; set; } = "cpu";   // cpu | ccd0 | ccd1 | gpu | gpu_hotspot | gpu_mem (see FanController.SourceTemp)
    public List<FanCurvePoint> Points  { get; set; } = new();
    public bool             Enabled    { get; set; } = false;
    public int              Hysteresis { get; set; } = 5;        // °C — prevents RPM flutter on cooldown
    public int              MinSpeed   { get; set; } = 0;        // % floor — fan never drops below this

    static readonly HashSet<string> Sources = new() { "cpu", "ccd0", "ccd1", "gpu", "gpu_hotspot", "gpu_mem" };

    /// <summary>
    /// Makes a curve safe to use, whatever the file or the page sent: no null lists,
    /// temperatures 0–120 °C, speeds 0–100 %, at most 16 points, a known temperature source.
    /// </summary>
    public FanCurve Normalized()
    {
        Points = (Points ?? new()).Where(p => p != null)
            .Select(p => new FanCurvePoint { Temp = Math.Clamp(p.Temp, 0, 120), Speed = Math.Clamp(p.Speed, 0, 100) })
            .Take(16).ToList();
        FanName    ??= "";
        TempSource = TempSource != null && Sources.Contains(TempSource) ? TempSource : "cpu";
        Hysteresis = Math.Clamp(Hysteresis, 0, 20);
        MinSpeed   = Math.Clamp(MinSpeed, 0, 100);
        return this;
    }
}

// ─── Main window placement (restored on next launch) ──────────────────────────
public class WindowPlacement
{
    public int  X         { get; set; }
    public int  Y         { get; set; }
    public int  Width     { get; set; }
    public int  Height    { get; set; }
    public bool Maximized { get; set; }
}

// ─── Main config ───────────────────────────────────────────────────────────────
public class AppConfig
{
    // Color theme keys → hex values (mirrors JS COLOR_DEFAULTS)
    public Dictionary<string, string> Colors   { get; set; } = new();

    // Page settings as strings (updateInterval, avgWindow, threadBusy, navOrder, zoom, ...).
    // Pages read and write their own keys, so new settings need no change here.
    public Dictionary<string, string> Settings { get; set; } = new();

    // Last selected theme preset index (for reset). Sent to the pages at startup;
    // nothing writes it back yet, so it stays 0 — the pages keep the real value in localStorage.
    public int LastPresetIdx { get; set; } = 0;

    // Saved fan curves
    public List<FanCurve> FanCurves { get; set; } = new();

    // Active fan profile: custom (your own modes / curves) | silent | balanced | performance
    public string FanProfile { get; set; } = "custom";

    // Fans the user marked as pumps (for headers whose name doesn't say "Pump" / "AIO")
    public List<string> PumpFans { get; set; } = new();

    // Main window size/position from the last session
    public WindowPlacement? Window { get; set; }

    // Saved color profiles (slot 1-3 → color key → hex)
    public Dictionary<int, Dictionary<string, string>> ColorProfiles { get; set; } = new();

    // ─── Paths ────────────────────────────────────────────────────────────────
    // ✌ V was here — 100% of this section is still my original code
    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vexis");

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");
    private static string BackupPath => ConfigPath + ".bak";   // the previous good file
    private static readonly object _saveLock = new();

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented            = true,
        DefaultIgnoreCondition   = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    // ─── Load ─────────────────────────────────────────────────────────────────
    public static AppConfig Load()
    {
        // The main file first; if it's damaged (e.g. power lost while saving), the backup
        foreach (var path in new[] { ConfigPath, BackupPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                // Unknown keys are ignored and missing ones keep their defaults, so an
                // older or newer Vexis can read the same file
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, _opts);
                if (cfg == null) continue;
                if (path == BackupPath) Console.WriteLine("[config] config.json was damaged — loaded the backup");
                return cfg.Normalized();
            }
            catch (Exception ex)
            {
                // Try the next file; with neither, start with defaults (overwritten on the next Save)
                Console.WriteLine($"[config] Load error ({Path.GetFileName(path)}): {ex.Message}");
            }
        }
        return new();
    }

    // A hand-edited or damaged file can contain nulls where lists are expected; fix them
    // here once, so nothing else has to check
    private AppConfig Normalized()
    {
        Colors        ??= new();
        Settings      ??= new();
        ColorProfiles ??= new();
        PumpFans      ??= new();
        FanProfile    ??= "custom";
        FanCurves = (FanCurves ?? new()).Where(c => c != null).Select(c => c.Normalized()).ToList();
        return this;
    }

    // Set by "Reset to factory settings" so nothing writes the file back while Vexis restarts
    public static bool SavesDisabled { get; set; }

    /// <summary>Deletes the saved settings (config.json). Logs, recordings and crash files stay.</summary>
    public static void DeleteSaved()
    {
        SavesDisabled = true;
        // The backup too, or the next start would load it and undo the reset
        foreach (var path in new[] { ConfigPath, BackupPath, ConfigPath + ".tmp" })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Console.WriteLine($"[config] Delete error: {ex.Message}"); }
        }
    }

    // ─── Save ─────────────────────────────────────────────────────────────────
    // ✌ V was here — 92% of this section is still my original code
    public void Save()
    {
        if (SavesDisabled) return;
        try
        {
            // Write a new file, then swap it in (keeping the old one as .bak), so a crash or
            // power cut mid-save never leaves a half-written config.json. One save at a time.
            lock (_saveLock)
            {
                Directory.CreateDirectory(ConfigDir);
                string tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, _opts));
                if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, BackupPath, ignoreMetadataErrors: true);
                else File.Move(tmp, ConfigPath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[config] Save error: {ex.Message}");
        }
    }

    // ─── Fan curve helpers ─────────────────────────────────────────────────────
    // ✌ V was here — 96% of this section is still my original code
    public FanCurve? GetCurve(string fanName) =>
        FanCurves.FirstOrDefault(c => c.FanName == fanName);

    public void UpsertCurve(FanCurve curve)
    {
        curve.Normalized();
        var idx = FanCurves.FindIndex(c => c.FanName == curve.FanName);
        if (idx >= 0) FanCurves[idx] = curve;
        else          FanCurves.Add(curve);
        Save();
    }

    /// <summary>
    /// Interpolate the curve to get a speed% for the given temperature.
    /// </summary>
    public static int InterpolateCurve(List<FanCurvePoint> points, float tempC)
    {
        // No points: a middle speed is safer than 0 for a fan that's meant to be on a curve
        if (points.Count == 0) return 50;
        points = points.OrderBy(p => p.Temp).ToList();

        // Below the first point / above the last: hold that point's speed
        if (tempC <= points[0].Temp)  return points[0].Speed;
        if (tempC >= points[^1].Temp) return points[^1].Speed;

        // Otherwise find the two points either side and draw a straight line between them

        for (int i = 0; i < points.Count - 1; i++)
        {
            var a = points[i]; var b = points[i + 1];
            if (tempC >= a.Temp && tempC <= b.Temp)
            {
                float t = (tempC - a.Temp) / (float)(b.Temp - a.Temp);
                return (int)Math.Round(a.Speed + t * (b.Speed - a.Speed));
            }
        }
        return 50;   // only reachable with a NaN temperature
    }
}
