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

public class PumpMarkTests
{
    [Fact]
    public void Pumps_are_found_by_name_or_by_the_users_mark()
    {
        try
        {
            FanController.LoadMarkedPumps(new[] { "Fan #3" });
            Assert.True(new FanEntry { Name = "Pump Fan" }.IsPump);       // by name
            Assert.True(new FanEntry { Name = "AIO Pump" }.PumpByName);
            Assert.True(new FanEntry { Name = "fan #3" }.IsPump);         // marked (case doesn't matter)
            Assert.False(new FanEntry { Name = "fan #3" }.PumpByName);
            Assert.False(new FanEntry { Name = "Fan #2" }.IsPump);

            FanController.LoadMarkedPumps(null);                          // unmarked again
            Assert.False(new FanEntry { Name = "Fan #3" }.IsPump);
        }
        finally { FanController.LoadMarkedPumps(null); }
    }
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

// Temperatures the fans can trust (FanController): a missing, impossible or stale reading
// must never look "cool", and a sensor that stops working counts as hot.
public class FailSafeTemperatureTests
{
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(0f)]
    [InlineData(-5f)]
    [InlineData(200f)]
    public void Impossible_readings_are_not_used(float t) => Assert.Null(FanController.Usable(t));

    [Fact]
    public void Real_readings_are_used() => Assert.Equal(65f, FanController.Usable(65f));

    [Fact]
    public void A_sensor_that_stops_working_is_lost_after_a_short_wait()
    {
        var track = new FanController.TempTrack();
        Assert.False(track.Read(60f, 95f, 0).Lost);
        Assert.False(track.Read(null, 95f, 100).Lost);                              // one bad tick: not yet
        Assert.True(track.Read(null, 95f, 100 + FanController.LostAfterMs).Lost);  // gone for 2 s: lost
        Assert.False(track.Read(61f, 95f, 5000).Lost);                              // back again
    }

    [Fact]
    public void A_sensor_this_PC_never_had_is_not_lost()
    {
        var track = new FanController.TempTrack();
        var r = track.Read(null, 95f, 60_000);
        Assert.False(r.Seen);
        Assert.False(r.Lost);
    }

    [Fact]
    public void A_lost_sensor_turns_the_guard_on() =>
        Assert.True(FanController.NextGuard(false, 10f, new FanController.GuardReading(null, 95f, Seen: true, Lost: true)));

    [Fact]
    public void A_missing_reading_never_turns_the_guard_off() =>
        // Before: a missing CPU temperature became 0 °C and released the guard
        Assert.True(FanController.NextGuard(true, 10f, new FanController.GuardReading(null, 95f, Seen: true, Lost: false)));

    [Fact]
    public void A_sensor_this_PC_never_had_does_not_hold_the_guard() =>
        // e.g. a GPU without a hot-spot sensor: the core reading alone decides
        Assert.False(FanController.NextGuard(true, 8f,
            new FanController.GuardReading(70f, 90f),
            new FanController.GuardReading(null, 100f, Seen: false)));
}

public class FanPairingTests
{
    [Theory]
    [InlineData("Fan #2", "Fan Control #2")]
    [InlineData("Fan #2", "Fan #2")]
    [InlineData("GPU Fan", "GPU Fan")]
    public void Rpm_and_control_sensor_names_match(string rpm, string control) =>
        Assert.Equal(FanController.Key(rpm), FanController.Key(control));

    [Fact]
    public void Different_headers_do_not_match() =>
        Assert.NotEqual(FanController.Key("Fan #1"), FanController.Key("Fan Control #2"));
}

public class CurveValidationTests
{
    [Fact]
    public void A_curve_with_null_points_becomes_an_empty_curve()
    {
        var c = System.Text.Json.JsonSerializer.Deserialize<FanCurve>("""{"FanName":"Fan #1","Enabled":true,"Points":null}""")!;
        c.Normalized();
        Assert.NotNull(c.Points);
        Assert.Empty(c.Points);
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        var c = new FanCurve
        {
            TempSource = "toaster", Hysteresis = 500, MinSpeed = -3,
            Points = new() { new() { Temp = -40, Speed = 250 }, new() { Temp = 999, Speed = -1 } }
        }.Normalized();
        Assert.Equal("cpu", c.TempSource);
        Assert.Equal(20, c.Hysteresis);
        Assert.Equal(0, c.MinSpeed);
        Assert.Equal((0, 100), (c.Points[0].Temp, c.Points[0].Speed));
        Assert.Equal((120, 0), (c.Points[1].Temp, c.Points[1].Speed));
    }

    [Fact]
    public void At_most_16_points_are_kept() =>
        Assert.Equal(16, new FanCurve { Points = Enumerable.Range(0, 40).Select(i => new FanCurvePoint { Temp = i, Speed = i }).ToList() }.Normalized().Points.Count);
}

public class DuplicateRootTests
{
    [Fact]
    public void The_same_folder_twice_and_folders_inside_another_root_are_scanned_once()
    {
        string root = Path.Combine(Path.GetTempPath(), "vexis-roots-" + Guid.NewGuid().ToString("N"));
        string sub  = Path.Combine(root, "photos");
        Directory.CreateDirectory(sub);
        try
        {
            var roots = DuplicateFinder.DistinctRoots(new[] { root, root + Path.DirectorySeparatorChar, sub }, r => r);
            Assert.Equal(new[] { root }, roots);
        }
        finally { Directory.Delete(root, true); }
    }
}

public class WatchdogTests
{
    [Fact]
    public void Trips_only_after_fan_updates_stop_for_the_limit()
    {
        Assert.False(FanController.WatchdogShouldTrip(lastUpdateAt: 1_000, now: 1_000 + FanController.WatchdogMs));
        Assert.True (FanController.WatchdogShouldTrip(lastUpdateAt: 1_000, now: 1_001 + FanController.WatchdogMs));
    }
}

public class ProcessIdentityTests
{
    [Theory]
    [InlineData(1_700_000_000_000, 1_700_000_000_000, true)]    // same process
    [InlineData(1_700_000_000_000, 1_700_000_000_001, true)]    // rounding
    [InlineData(1_700_000_000_000, 1_700_000_090_000, false)]   // PID reused by a newer process
    [InlineData(0, 1_700_000_000_000, true)]                    // page didn't know: name check decides
    [InlineData(1_700_000_000_000, 0, true)]                    // Windows won't say now: name check decides
    public void A_reused_pid_is_caught_by_its_start_time(long shown, long now, bool same) =>
        Assert.Equal(same, ProcessMonitor.SameStart(shown, now));
}
