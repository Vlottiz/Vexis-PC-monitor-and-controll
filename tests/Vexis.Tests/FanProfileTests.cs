using Xunit;

public class FanProfileTests
{
    private static readonly string[] Presets = { "silent", "balanced", "performance" };

    [Theory]
    [InlineData("silent", "silent")]
    [InlineData("PERFORMANCE", "performance")]
    [InlineData("turbo", "custom")]
    [InlineData(null, "custom")]
    public void Unknown_names_become_custom(string? given, string expected) =>
        Assert.Equal(expected, FanProfiles.Normalize(given));

    [Fact]
    public void Custom_has_no_preset_curve() =>
        Assert.Null(FanProfiles.CurveFor("custom", "CPU Fan", isGpu: false, isPump: false));

    [Fact]
    public void Presets_give_an_enabled_curve_on_the_right_sensor()
    {
        foreach (var p in Presets)
        {
            var board = FanProfiles.CurveFor(p, "System Fan #1", false, false)!;
            var gpu   = FanProfiles.CurveFor(p, "GPU Fan", true, false)!;
            Assert.True(board.Enabled);
            Assert.Equal("cpu", board.TempSource);
            Assert.Equal("gpu", gpu.TempSource);
        }
    }

    [Fact]
    public void Every_preset_reaches_full_speed_when_hot()
    {
        foreach (var p in Presets)
        foreach (var (gpu, pump) in new[] { (false, false), (true, false), (false, true) })
        {
            var c = FanProfiles.CurveFor(p, "fan", gpu, pump)!;
            Assert.Equal(100, AppConfig.InterpolateCurve(c.Points, 95f));
        }
    }

    [Fact]
    public void Pumps_never_drop_below_sixty_percent()
    {
        foreach (var p in Presets)
        {
            var c = FanProfiles.CurveFor(p, "Pump Fan #1", false, true)!;
            Assert.True(c.MinSpeed >= 60, $"{p} pump minimum is {c.MinSpeed}%");
            Assert.True(AppConfig.InterpolateCurve(c.Points, 20f) >= 60);
        }
    }

    [Fact]
    public void Louder_presets_are_never_quieter_at_any_temperature()
    {
        foreach (var gpu in new[] { false, true })
            for (int t = 20; t <= 100; t++)
            {
                int s = AppConfig.InterpolateCurve(FanProfiles.CurveFor("silent", "f", gpu, false)!.Points, t);
                int b = AppConfig.InterpolateCurve(FanProfiles.CurveFor("balanced", "f", gpu, false)!.Points, t);
                int f = AppConfig.InterpolateCurve(FanProfiles.CurveFor("performance", "f", gpu, false)!.Points, t);
                Assert.True(s <= b && b <= f, $"{(gpu ? "GPU" : "board")} at {t}°C: silent {s}, balanced {b}, performance {f}");
            }
    }
}
