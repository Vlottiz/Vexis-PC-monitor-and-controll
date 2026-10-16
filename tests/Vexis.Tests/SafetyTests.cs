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

// Over-temperature guards: fans to 100% at the limit, back only after a real drop.
public class GuardTests
{
    [Fact]
    public void Switches_on_at_the_limit() =>
        Assert.True(FanController.NextGuard(false, 10f, (95f, FanController.CpuLimit)));

    [Fact]
    public void Stays_off_below_the_limit() =>
        Assert.False(FanController.NextGuard(false, 10f, (94.9f, FanController.CpuLimit)));

    [Fact]
    public void Stays_on_until_it_has_cooled_by_the_release_margin()
    {
        Assert.True(FanController.NextGuard(true, 10f, (90f, 95f)));    // 5 °C under: still on
        Assert.True(FanController.NextGuard(true, 10f, (85f, 95f)));    // exactly at the margin: still on
        Assert.False(FanController.NextGuard(true, 10f, (84.9f, 95f))); // cooled off
    }

    [Fact]
    public void Gpu_guard_trips_on_either_core_or_hot_spot() =>
        Assert.True(FanController.NextGuard(false, 8f, (70f, FanController.GpuCoreLimit), (101f, FanController.GpuHotspotLimit)));

    [Fact]
    public void Gpu_guard_releases_only_when_both_have_cooled() =>
        Assert.True(FanController.NextGuard(true, 8f, (70f, 90f), (95f, 100f)));   // hot spot still within 8 °C
}

// Commands are only accepted from Vexis's own pages.
public class AppPageTests
{
    [Theory]
    [InlineData("https://vexis.local/home.html")]
    [InlineData("https://vexis.local/fans.html?popout=1")]
    [InlineData("https://VEXIS.local/rgb.html")]
    public void Own_pages_are_trusted(string url) => Assert.True(SafeLinks.IsAppPage(url));

    [Theory]
    [InlineData(null)]
    [InlineData("http://vexis.local/home.html")]           // not https
    [InlineData("https://vexis.local.evil.com/home.html")]
    [InlineData("https://evil.com/?vexis.local")]
    [InlineData("https://vexis.local:8443/home.html")]
    [InlineData("file:///C:/Program%20Files/Vexis/home.html")]
    [InlineData("about:blank")]
    public void Anything_else_is_not(string? url) => Assert.False(SafeLinks.IsAppPage(url));
}

// Paths the pages can send to the duplicate finder and the recordings page.
public sealed class PathCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vexis-path-tests-" + Guid.NewGuid().ToString("N"));
    public PathCheckTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void A_real_folder_can_be_scanned() =>
        Assert.Equal(Path.GetFullPath(_dir), DuplicateFinder.CheckFolder(_dir));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/folder")]
    [InlineData("*")]
    public void Anything_else_is_not_scanned(string? path) =>
        Assert.Null(DuplicateFinder.CheckFolder(path));

    [Fact]
    public void A_missing_folder_is_not_scanned() =>
        Assert.Null(DuplicateFinder.CheckFolder(Path.Combine(_dir, "missing")));

    [Fact]
    public void Only_files_from_the_last_scan_can_be_revealed_or_recycled()
    {
        var data = new byte[5000]; new Random(7).NextBytes(data);
        string a = Path.Combine(_dir, "a.bin"), b = Path.Combine(_dir, "b.bin"), other = Path.Combine(_dir, "other.txt");
        File.WriteAllBytes(a, data); File.WriteAllBytes(b, data); File.WriteAllText(other, "not a duplicate");

        var finder = new DuplicateFinder();
        var done = new TaskCompletionSource();
        finder.Start(new DuplicateFinder.Options(new() { _dir }, 0, false), _ => { }, _ => done.TrySetResult());
        Assert.True(done.Task.Wait(TimeSpan.FromSeconds(30)));

        Assert.True(finder.IsInResults(a));
        Assert.False(finder.IsInResults(other));                       // exists, but wasn't a duplicate
        Assert.False(finder.IsInResults(Path.Combine(_dir, "*.bin")));  // wildcards
        Assert.False(finder.IsInResults("a.bin"));                      // not a full path

        var (removed, _, skipped) = finder.Recycle(new() { other });     // never touched
        Assert.Equal(0, removed);
        Assert.True(File.Exists(other));
    }

    [Theory]
    [InlineData("desktop.ini")]
    [InlineData("notes.csv")]
    [InlineData("vexis-run.exe")]
    public void Only_vexis_recordings_can_be_opened(string name) =>
        Assert.Null(CsvRecorder.ResolveFile(name));
}
