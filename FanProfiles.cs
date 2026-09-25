/// <summary>
/// Built-in fan profiles. A preset gives every controllable fan a curve:
/// motherboard / case fans follow the CPU temperature, GPU fans the GPU core
/// temperature, and pumps (AIO / "Pump" headers) keep a high floor because
/// slowing a pump down raises CPU temperature quickly.
/// "custom" means each fan keeps its own mode or saved curve.
/// </summary>
public static class FanProfiles
{
    public static readonly string[] All = { "custom", "silent", "balanced", "performance" };

    public static string Normalize(string? name) =>
        All.Contains(name?.ToLowerInvariant()) ? name!.ToLowerInvariant() : "custom";

    public static string Title(string name) => Normalize(name) switch
    {
        "silent"      => "Silent",
        "balanced"    => "Balanced",
        "performance" => "Performance",
        _             => "Custom"
    };

    private static List<FanCurvePoint> P(params (int t, int s)[] pts) =>
        pts.Select(p => new FanCurvePoint { Temp = p.t, Speed = p.s }).ToList();

    // (points, hysteresis °C, minimum %) for case / CPU fans, following CPU temperature
    private static readonly Dictionary<string, (List<FanCurvePoint> pts, int hyst, int min)> Board = new()
    {
        ["silent"]      = (P((40, 20), (60, 32), (75, 55), (85, 100)), 6, 20),
        ["balanced"]    = (P((35, 30), (55, 45), (70, 70), (82, 100)), 4, 25),
        ["performance"] = (P((30, 45), (50, 60), (65, 85), (75, 100)), 3, 35),
    };

    // GPU fans, following GPU core temperature (GPUs run warmer by design)
    private static readonly Dictionary<string, (List<FanCurvePoint> pts, int hyst, int min)> Gpu = new()
    {
        ["silent"]      = (P((50, 25), (70, 40), (80, 65), (88, 100)), 5, 0),
        ["balanced"]    = (P((45, 30), (65, 50), (75, 75), (85, 100)), 4, 25),
        ["performance"] = (P((40, 40), (60, 65), (70, 85), (80, 100)), 3, 35),
    };

    // Pumps: flat, with a floor that never starves the CPU block
    private static readonly Dictionary<string, (List<FanCurvePoint> pts, int hyst, int min)> Pump = new()
    {
        ["silent"]      = (P((40, 60), (70, 70), (85, 100)), 5, 60),
        ["balanced"]    = (P((40, 70), (70, 85), (85, 100)), 5, 70),
        ["performance"] = (P((30, 100)), 0, 100),
    };

    public static FanCurve? CurveFor(string profile, string fanName, bool isGpu, bool isPump)
    {
        profile = Normalize(profile);
        if (profile == "custom") return null;
        var set = isPump ? Pump : isGpu ? Gpu : Board;
        var (pts, hyst, min) = set[profile];
        return new FanCurve
        {
            FanName = fanName, Enabled = true, Points = pts, Hysteresis = hyst, MinSpeed = min,
            TempSource = isGpu ? "gpu" : "cpu"
        };
    }

    /// <summary>The preset curves, for drawing on the Fan Control page.</summary>
    public static object Describe() => All.Where(p => p != "custom").ToDictionary(p => p, p => new
    {
        board = Board[p].pts, gpu = Gpu[p].pts, pump = Pump[p].pts,
        boardMin = Board[p].min, gpuMin = Gpu[p].min, pumpMin = Pump[p].min
    });
}
