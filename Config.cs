using System.Text.Json;
using System.Text.Json.Serialization;

// ─── Fan curve data ────────────────────────────────────────────────────────────
public class FanCurvePoint
{
    public int Temp  { get; set; }  // °C
    public int Speed { get; set; }  // 0–100%
}

public class FanCurve
{
    public string           FanName    { get; set; } = "";
    public string           TempSource { get; set; } = "cpu";   // cpu | ccd0 | ccd1 | gpu
    public List<FanCurvePoint> Points  { get; set; } = new();
    public bool             Enabled    { get; set; } = false;
    public int              Hysteresis { get; set; } = 5;        // °C — prevents RPM flutter on cooldown
    public int              MinSpeed   { get; set; } = 0;        // % floor — fan never drops below this
}

// ─── Main config ───────────────────────────────────────────────────────────────
public class AppConfig
{
    // Color theme keys → hex values (mirrors JS COLOR_DEFAULTS)
    public Dictionary<string, string> Colors   { get; set; } = new();

    // Display settings (updateInterval, avgWindow, gearBlinkDuration)
    public Dictionary<string, string> Settings { get; set; } = new();

    // Last selected theme preset index (for reset)
    public int LastPresetIdx { get; set; } = 0;

    // Saved fan curves
    public List<FanCurve> FanCurves { get; set; } = new();

    // Saved color profiles (slot 1-3)
    public Dictionary<int, Dictionary<string, string>> ColorProfiles { get; set; } = new();

    // ─── Paths ────────────────────────────────────────────────────────────────
    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vexis");

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented            = true,
        DefaultIgnoreCondition   = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    // ─── Load ─────────────────────────────────────────────────────────────────
    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json, _opts) ?? new();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[config] Load error: {ex.Message}");
        }
        return new();
    }

    // ─── Save ─────────────────────────────────────────────────────────────────
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, _opts));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[config] Save error: {ex.Message}");
        }
    }

    // ─── Fan curve helpers ─────────────────────────────────────────────────────
    public FanCurve? GetCurve(string fanName) =>
        FanCurves.FirstOrDefault(c => c.FanName == fanName);

    public void UpsertCurve(FanCurve curve)
    {
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
        if (points.Count == 0) return 50;
        points = points.OrderBy(p => p.Temp).ToList();

        if (tempC <= points[0].Temp)  return points[0].Speed;
        if (tempC >= points[^1].Temp) return points[^1].Speed;

        for (int i = 0; i < points.Count - 1; i++)
        {
            var a = points[i]; var b = points[i + 1];
            if (tempC >= a.Temp && tempC <= b.Temp)
            {
                float t = (tempC - a.Temp) / (float)(b.Temp - a.Temp);
                return (int)Math.Round(a.Speed + t * (b.Speed - a.Speed));
            }
        }
        return 50;
    }
}
