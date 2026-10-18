using System.Collections.Concurrent;
using System.Reflection;
using LibreHardwareMonitor.Hardware;
using Xunit;

// The real FanController.RunHealthCheck on a worker thread against the real Update on
// another, as in the app (health check on Task.Run, Update on the UI poll timer), using
// fake LibreHardwareMonitor sensors that record every speed written to the "fan chip".
public class HealthCheckConcurrencyTests
{
    public class Fake : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = (_, _) => null;
        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            var r = Handler(m!, args);
            return r ?? (m!.ReturnType.IsValueType && m.ReturnType != typeof(void) ? Activator.CreateInstance(m.ReturnType) : null);
        }
    }
    static T Make<T>(Func<MethodInfo, object?[]?, object?> h)
    {
        var p = DispatchProxy.Create<T, Fake>();
        ((Fake)(object)p!).Handler = h;
        return p;
    }

    // Every write to the fake fan, in order (-1 = handed back to the BIOS)
    readonly ConcurrentQueue<float> _writes = new();

    FanEntry MakeFan()
    {
        var hw = Make<IHardware>((m, _) => m.Name switch
        {
            "get_HardwareType" => HardwareType.SuperIO,
            "get_Name"         => "Fake chip",
            "get_Identifier"   => new Identifier("fake"),
            "get_Sensors"      => Array.Empty<ISensor>(),
            "get_SubHardware"  => Array.Empty<IHardware>(),
            _ => null
        });
        var control = Make<IControl>((m, a) =>
        {
            if (m.Name == "SetSoftware") _writes.Enqueue((float)a![0]!);
            if (m.Name == "SetDefault")  _writes.Enqueue(-1);
            return null;
        });
        var rpm  = Make<ISensor>((m, _) => m.Name switch
        {
            "get_Value" => (float?)1200f, "get_Hardware" => hw, "get_Name" => "CPU Fan", "get_SensorType" => SensorType.Fan, _ => null
        });
        var ctrl = Make<ISensor>((m, _) => m.Name switch
        {
            "get_Control" => control, "get_Hardware" => hw, "get_Name" => "CPU Fan", "get_SensorType" => SensorType.Control, _ => null
        });
        return new FanEntry { Name = "CPU Fan", RpmSensor = rpm, CtrlSensor = ctrl, Mode = "manual", ManualPct = 40 };
    }

    static SensorData Cpu(float t) => new() { cpu_temp = t, taken_ms = Environment.TickCount64 };

    [Fact]
    public void A_guard_that_trips_during_the_check_stops_it_and_wins()
    {
        var fc = new FanController();
        fc.UseFansForTest(new[] { MakeFan() });
        var config = new AppConfig();
        fc.Update(Cpu(50), config);                          // cool: manual 40 %

        var check = Task.Run(() => fc.RunHealthCheck("CPU Fan"));
        var until = DateTime.Now.AddSeconds(5);
        while (!_writes.Contains(30f) && DateTime.Now < until) Thread.Sleep(10);   // first step written
        Assert.Contains(30f, _writes);

        // The CPU overheats; the poll keeps running alongside the check, as in the app
        while (!check.IsCompleted && DateTime.Now < until) { fc.Update(Cpu(99), config); Thread.Sleep(50); }
        for (int i = 0; i < 5; i++) { fc.Update(Cpu(99), config); Thread.Sleep(20); }

        Assert.True(check.IsCompleted, "the check should stop within a few hundred ms");
        Assert.Equal("STOPPED", check.Result.Status);
        var w = _writes.ToList();
        int firstFull = w.IndexOf(100f);
        Assert.True(firstFull >= 0, "the fan was never set to 100 %");
        Assert.All(w.Skip(firstFull), v => Assert.Equal(100f, v));   // nothing lower after the guard
        Assert.DoesNotContain(60f, w);                                  // the second step never ran
    }

    [Fact]
    public void A_check_does_not_start_while_the_guard_is_on()
    {
        var fc = new FanController();
        fc.UseFansForTest(new[] { MakeFan() });
        var config = new AppConfig();
        fc.Update(Cpu(99), config);                          // guard on: fan at 100 %
        var before = _writes.Count;

        var r = fc.RunHealthCheck("CPU Fan");

        Assert.Equal("SKIPPED", r.Status);
        Assert.Equal(before, _writes.Count);                 // the check wrote nothing
        Assert.Equal(100f, _writes.Last());
    }
}
