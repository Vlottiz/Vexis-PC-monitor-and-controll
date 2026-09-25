using Xunit;

// Temperature → fan speed. A wrong number here means a fan that is too slow
// under load or needlessly loud, so these cover the edges of every curve.
public class FanCurveTests
{
    private static List<FanCurvePoint> Curve(params (int t, int s)[] pts) =>
        pts.Select(p => new FanCurvePoint { Temp = p.t, Speed = p.s }).ToList();

    [Fact]
    public void Below_the_first_point_uses_the_first_speed() =>
        Assert.Equal(30, AppConfig.InterpolateCurve(Curve((40, 30), (80, 100)), 20f));

    [Fact]
    public void Above_the_last_point_uses_the_last_speed() =>
        Assert.Equal(100, AppConfig.InterpolateCurve(Curve((40, 30), (80, 100)), 95f));

    [Theory]
    [InlineData(40f, 30)]
    [InlineData(60f, 65)]      // halfway between 30 % and 100 %
    [InlineData(44f, 37)]      // 30 + 0.1 × 70
    [InlineData(80f, 100)]
    public void Between_points_is_a_straight_line(float temp, int expected) =>
        Assert.Equal(expected, AppConfig.InterpolateCurve(Curve((40, 30), (80, 100)), temp));

    [Fact]
    public void Point_order_does_not_matter() =>
        Assert.Equal(AppConfig.InterpolateCurve(Curve((40, 30), (60, 50), (80, 100)), 70f),
                     AppConfig.InterpolateCurve(Curve((80, 100), (40, 30), (60, 50)), 70f));

    [Fact]
    public void Empty_curve_falls_back_to_a_safe_middle_speed() =>
        Assert.Equal(50, AppConfig.InterpolateCurve(new List<FanCurvePoint>(), 70f));

    [Fact]
    public void Single_point_curve_is_flat()
    {
        Assert.Equal(100, AppConfig.InterpolateCurve(Curve((30, 100)), 10f));
        Assert.Equal(100, AppConfig.InterpolateCurve(Curve((30, 100)), 90f));
    }
}

// Hysteresis stops fans revving up and down when a temperature hovers around a
// curve point: speeding up is immediate, slowing down waits for a real drop.
public class FanHysteresisTests
{
    private static FanCurve Curve(int hysteresis = 5, int min = 0) => new()
    {
        Enabled = true, Hysteresis = hysteresis, MinSpeed = min,
        Points = new() { new() { Temp = 40, Speed = 20 }, new() { Temp = 80, Speed = 100 } }
    };

    [Fact]
    public void First_reading_sets_the_curve_speed()
    {
        var fan = new FanEntry();
        Assert.Equal(60, FanController.CurveSpeed(fan, Curve(), 60f));
    }

    [Fact]
    public void Speeds_up_straight_away()
    {
        var fan = new FanEntry();
        FanController.CurveSpeed(fan, Curve(), 60f);
        Assert.Equal(80, FanController.CurveSpeed(fan, Curve(), 70f));
    }

    [Fact]
    public void Holds_speed_while_cooling_less_than_the_hysteresis()
    {
        var fan = new FanEntry();
        FanController.CurveSpeed(fan, Curve(hysteresis: 5), 70f);                 // 80 %
        Assert.Equal(80, FanController.CurveSpeed(fan, Curve(hysteresis: 5), 67f)); // only 3 °C cooler
        Assert.Equal(80, FanController.CurveSpeed(fan, Curve(hysteresis: 5), 66f));
    }

    [Fact]
    public void Slows_down_once_it_has_cooled_by_the_hysteresis()
    {
        var fan = new FanEntry();
        FanController.CurveSpeed(fan, Curve(hysteresis: 5), 70f);                  // 80 % at 70 °C
        Assert.Equal(70, FanController.CurveSpeed(fan, Curve(hysteresis: 5), 65f)); // 5 °C cooler → follow the curve
    }

    [Fact]
    public void Zero_hysteresis_follows_the_curve_exactly()
    {
        var fan = new FanEntry();
        FanController.CurveSpeed(fan, Curve(hysteresis: 0), 70f);
        Assert.Equal(78, FanController.CurveSpeed(fan, Curve(hysteresis: 0), 69f));
    }

    [Fact]
    public void Never_goes_below_the_minimum_speed()
    {
        var fan = new FanEntry();
        Assert.Equal(35, FanController.CurveSpeed(fan, Curve(min: 35), 20f));   // curve says 20 %
    }

    [Fact]
    public void Result_is_always_a_valid_percentage()
    {
        var fan = new FanEntry();
        var c = Curve(min: 150);
        Assert.InRange(FanController.CurveSpeed(fan, c, 50f), 0, 100);
    }
}
