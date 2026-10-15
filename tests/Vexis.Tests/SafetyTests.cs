using Xunit;

// Checks that stop a page or a bad reading from doing something unsafe.
public class SafeLinkTests
{
    [Theory]
    [InlineData("https://github.com/Vlottiz/Vexis-PC-monitor-and-controll")]
    [InlineData("https://openrgb.org")]
    [InlineData("  https://buymeacoffee.com/reyobu  ")]
    public void Https_web_pages_are_opened(string url) =>
        Assert.True(SafeLinks.IsWebLink(url, out _));

    // Shell-execute would run or open any of these
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData("calc.exe")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData(@"\\server\share\run.bat")]
    [InlineData("http://example.com")]            // plain http: not used by Vexis, refused
    [InlineData("ms-settings:privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://")]
    public void Anything_else_is_refused(string? url) =>
        Assert.False(SafeLinks.IsWebLink(url, out _));
}

public class FanSafeSpeedTests
{
    [Theory]
    [InlineData(45f, false, 45)]
    [InlineData(-20f, false, 0)]
    [InlineData(250f, false, 100)]
    [InlineData(0f, false, 0)]             // case fans may stop if the user asks
    public void Speeds_are_kept_between_0_and_100(float asked, bool pump, int sent) =>
        Assert.Equal(sent, FanController.SafeSpeed(asked, pump));

    [Theory]
    [InlineData(0f)]
    [InlineData(30f)]
    [InlineData(49f)]
    public void Pumps_never_go_below_the_floor(float asked) =>
        Assert.Equal(FanController.PumpMinPct, FanController.SafeSpeed(asked, isPump: true));

    [Fact]
    public void Pumps_above_the_floor_are_left_alone() =>
        Assert.Equal(80, FanController.SafeSpeed(80f, isPump: true));

    [Fact]
    public void A_broken_reading_means_full_speed() =>
        Assert.Equal(100, FanController.SafeSpeed(float.NaN, isPump: false));
}

// The headline CPU temperature drives the display, fan curves and alerts.
public class CpuTempTests
{
    private static float? Pick(params (string, float)[] temps) => SensorService.PickCpuTemp(temps);

    // Intel lists per-core sensors before the package. The package must win anyway.
    [Fact]
    public void Intel_uses_the_package_not_the_first_core() =>
        Assert.Equal(64f, Pick(("CPU Core #1", 52f), ("CPU Core #2", 58f), ("CPU Package", 64f), ("Core Max", 61f), ("Core Average", 55f)));

    [Fact]
    public void Amd_uses_tctl_tdie() =>
        Assert.Equal(71f, Pick(("Core (Tctl/Tdie)", 71f), ("Package", 69f)));

    [Fact]
    public void Core_average_when_there_is_no_package() =>
        Assert.Equal(55f, Pick(("CPU Core #1", 52f), ("Core Average", 55f)));

    [Fact]
    public void Falls_back_to_any_plausible_reading() =>
        Assert.Equal(48f, Pick(("Something", 5f), ("Core #1", 48f)));

    [Fact]
    public void Nothing_usable_means_no_reading() =>
        Assert.Null(Pick(("Core #1", 0.5f)));
}
