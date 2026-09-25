using System.Runtime.InteropServices;

/// <summary>
/// Which logical processors (hardware threads) belong to which physical core,
/// straight from Windows (GetLogicalProcessorInformationEx).
///
/// LibreHardwareMonitor names per-thread load sensors "CPU Core #1 Thread #2"
/// only when its own CPUID grouping works. On some CPUs (seen on a Ryzen 9
/// 9950X3D) it lists every thread as a separate "CPU Core #N", so the thread →
/// core mapping has to come from Windows instead. Works for AMD and Intel,
/// including hybrid CPUs where P-cores have 2 threads and E-cores 1.
/// </summary>
public static class CpuTopology
{
    /// <summary>Physical core index (Windows order) for each logical processor, or null if unavailable.</summary>
    public static int[]? LogicalToCore { get; internal set; } = Read();
    public static int CoreCount => LogicalToCore == null ? 0 : LogicalToCore.Max() + 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
    private const int RelationProcessorCore = 0;

    private static int[]? Read()
    {
        try
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
            if (len == 0) return null;
            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len)) return null;
                var map = new SortedDictionary<int, int>();   // logical index -> core index
                int core = 0;
                for (uint off = 0; off < len;)
                {
                    IntPtr rec = buf + (int)off;
                    // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship(4) Size(4) then
                    // PROCESSOR_RELATIONSHIP: Flags(1) EfficiencyClass(1) Reserved(20) GroupCount(2)
                    // GROUP_AFFINITY[GroupCount] at +32: Mask(8) Group(2) Reserved(6)
                    uint size = (uint)Marshal.ReadInt32(rec, 4);
                    if (size == 0) break;
                    if (Marshal.ReadInt32(rec, 0) == RelationProcessorCore)
                    {
                        int groups = Marshal.ReadInt16(rec, 30);
                        for (int g = 0; g < groups; g++)
                        {
                            IntPtr aff = rec + 32 + g * 16;
                            ulong mask = (ulong)Marshal.ReadInt64(aff, 0);
                            int group = Marshal.ReadInt16(aff, 8);
                            for (int bit = 0; bit < 64; bit++)
                                if ((mask & (1UL << bit)) != 0) map[group * 64 + bit] = core;
                        }
                        core++;
                    }
                    off += size;
                }
                if (map.Count == 0) return null;
                var result = map.Values.ToArray();   // ordered by logical index
                Console.WriteLine($"[hw] Windows topology: {core} cores, {result.Length} threads");
                return result;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[hw] CPU topology unavailable: {ex.Message}");
            return null;
        }
    }
}
