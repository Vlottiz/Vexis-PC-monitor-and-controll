using Xunit;

// Which physical core each per-thread load belongs to. Sensor names differ between
// CPUs and LibreHardwareMonitor versions, so each real naming scheme gets a test.
public class ThreadLoadTests
{
    private static (Dictionary<int, float> core, Dictionary<int, SortedDictionary<int, float>> threads) Run(
        List<(string, float)> loads, Dictionary<int, (int, int)> map, bool hybrid, int[]? topo = null)
    {
        var core = new Dictionary<int, float>();
        var threads = new Dictionary<int, SortedDictionary<int, float>>();
        SensorService.AssignThreadLoads(loads, map, hybrid, topo, core, threads);
        return (core, threads);
    }

    [Fact]
    public void Ryzen_style_names_group_two_threads_per_core()
    {
        var map = Enumerable.Range(1, 16).ToDictionary(i => i, i => (i - 1, 0));          // "CPU Core #1..#16" clocks
        var loads = Enumerable.Range(1, 16).SelectMany(c => new[] { ($"CPU Core #{c} Thread #1", 10f * (c % 5)), ($"CPU Core #{c} Thread #2", 1f) }).ToList();
        var (core, threads) = Run(loads, map, hybrid: false);
        Assert.Equal(16, threads.Count);
        Assert.All(threads.Values, t => Assert.Equal(2, t.Count));
        Assert.Equal(32, threads.Values.Sum(t => t.Count));
        Assert.Equal(30f, core[2]);                                                       // core #3 → display 2, max of its threads
    }

    // i9-12900K: clocks are "P-Core #1..#8" and "E-Core #1..#8", but LHM numbers the
    // loads across the whole CPU. E-cores used to be dropped (x/16 instead of x/24).
    [Fact]
    public void Intel_hybrid_maps_whole_cpu_numbering_onto_P_and_E_cores()
    {
        var map = new Dictionary<int, (int, int)>();
        for (int i = 1; i <= 8; i++) map[i] = (i - 1, 0);                                  // P-Core #i  → display 0..7
        for (int i = 1; i <= 8; i++) map[1000 + i] = (7 + i, 0);                           // E-Core #i  → display 8..15
        var loads = new List<(string, float)>();
        for (int c = 1; c <= 8; c++) { loads.Add(($"CPU Core #{c} Thread #1", 50f)); loads.Add(($"CPU Core #{c} Thread #2", 5f)); }
        for (int c = 9; c <= 16; c++) loads.Add(($"CPU Core #{c}", 20f + c));
        loads.Add(("CPU Total", 25f));                                                    // not a core, ignored

        var (core, threads) = Run(loads, map, hybrid: true);
        Assert.Equal(24, threads.Values.Sum(t => t.Count));
        Assert.All(Enumerable.Range(0, 8), d => Assert.Equal(2, threads[d].Count));        // P-cores: 2 threads
        Assert.All(Enumerable.Range(8, 8), d => Assert.Single(threads[d]));                // E-cores: 1 thread
        Assert.Equal(29f, core[8]);                                                        // "CPU Core #9" → first E-core
        Assert.Equal(36f, core[15]);                                                       // "CPU Core #16" → last E-core
    }

    [Fact]
    public void Intel_hybrid_also_accepts_P_and_E_named_loads()
    {
        var map = new Dictionary<int, (int, int)> { [1] = (0, 0), [1001] = (1, 0) };
        var loads = new List<(string, float)> { ("P-Core #1 Thread #1", 40f), ("P-Core #1 Thread #2", 10f), ("E-Core #1", 70f) };
        var (core, threads) = Run(loads, map, hybrid: true);
        Assert.Equal(2, threads[0].Count);
        Assert.Equal(70f, core[1]);
    }

    // Some LHM versions list one ungrouped load per logical processor; Windows says
    // which core each one is on (here SMT siblings are 0/1, 2/3, ...).
    [Fact]
    public void Ungrouped_loads_use_the_windows_topology()
    {
        var map = Enumerable.Range(1, 4).ToDictionary(i => i, i => (i - 1, 0));
        var loads = Enumerable.Range(1, 8).Select(i => ($"CPU Core #{i}", (float)i)).ToList();
        int[] topo = { 0, 0, 1, 1, 2, 2, 3, 3 };
        var (core, threads) = Run(loads, map, hybrid: false, topo);
        Assert.Equal(4, threads.Count);
        Assert.All(threads.Values, t => Assert.Equal(2, t.Count));
        Assert.Equal(8f, core[3]);
    }

    [Fact]
    public void Idle_threads_still_count()
    {
        var map = new Dictionary<int, (int, int)> { [1] = (0, 0) };
        var (_, threads) = Run(new() { ("CPU Core #1 Thread #1", 0f), ("CPU Core #1 Thread #2", 0f) }, map, false);
        Assert.Equal(2, threads[0].Count);
    }
}
