using System.Management;
using LibreHardwareMonitor.Hardware;

/// <summary>One drive on the Storage page.</summary>
public class DriveInfoData
{
    public string  name        { get; set; } = "";
    public string  kind        { get; set; } = "";   // "NVMe SSD", "SATA SSD", "HDD", "USB"...
    public string  letters     { get; set; } = "";   // "C: D:"
    public float?  temp        { get; set; }
    public float?  temp_warn   { get; set; }         // drive's own warning temperature (NVMe)
    public float?  temp_crit   { get; set; }
    public float?  life        { get; set; }         // % life remaining (SSD wear), 100 = new
    public float?  used_pct    { get; set; }
    public float?  total_gb    { get; set; }
    public float?  free_gb     { get; set; }
    public float?  read_rate   { get; set; }         // bytes/s
    public float?  write_rate  { get; set; }
    public float?  activity    { get; set; }         // % busy
    public float?  data_read   { get; set; }         // GB read over the drive's life
    public float?  data_written { get; set; }
    public float?  power_on_hours { get; set; }
    public float?  power_on_count { get; set; }
    public string  health      { get; set; } = "";   // good | worn | replace | hot | unknown
}

/// <summary>
/// Reads drives from LibreHardwareMonitor's storage sensors and adds what Windows
/// knows: drive letters and SSD / HDD / NVMe type. SMART is only read every
/// couple of seconds — it is a disk command, not a cheap register read.
/// </summary>
public static class StorageReader
{
    private static Dictionary<string, (string kind, string letters)>? _winInfo;
    private static DateTime _winInfoAt;

    public static DriveInfoData Read(IHardware hw)
    {
        var d = new DriveInfoData { name = hw.Name.Trim() };
        foreach (var s in hw.Sensors)
        {
            if (s.Value is not float v || float.IsNaN(v)) continue;
            switch (s.SensorType, s.Name)
            {
                case (SensorType.Temperature, "Temperature"):
                case (SensorType.Temperature, "Composite Temperature"): d.temp ??= v; break;
                case (SensorType.Temperature, "Warning Temperature"):   if (v > 0) d.temp_warn = v; break;
                case (SensorType.Temperature, "Critical Temperature"):  if (v > 0) d.temp_crit = v; break;
                case (SensorType.Level, "Life"):                        d.life = v; break;
                case (SensorType.Load, "Used Space"):                   d.used_pct = v; break;
                case (SensorType.Load, "Total Activity"):               d.activity = v; break;
                case (SensorType.Data, "Total Space"):                  d.total_gb = v; break;
                case (SensorType.Data, "Free Space"):                   d.free_gb = v; break;
                case (SensorType.Data, "Data Read"):                    d.data_read = v; break;
                case (SensorType.Data, "Data Written"):                 d.data_written = v; break;
                case (SensorType.Throughput, "Read Rate"):              d.read_rate = v; break;
                case (SensorType.Throughput, "Write Rate"):             d.write_rate = v; break;
                case (SensorType.Factor, "Power On Hours"):             d.power_on_hours = v; break;
                case (SensorType.Factor, "Power On Count"):             d.power_on_count = v; break;
            }
            // Other numbered temperature sensors (Temperature #1..) as a fallback
            if (s.SensorType == SensorType.Temperature && d.temp == null && s.Name.StartsWith("Temperature") && v is > 1 and < 120) d.temp = v;
        }

        var win = WindowsInfo();
        var match = win.FirstOrDefault(kv => Same(kv.Key, d.name));
        if (match.Key != null) { d.kind = match.Value.kind; d.letters = match.Value.letters; }

        float hotAt = d.temp_warn ?? (d.kind.Contains("HDD") ? 55 : 70);
        d.health = d.temp is float t && t >= hotAt ? "hot"
                 : d.life is float l ? (l < 10 ? "replace" : l < 50 ? "worn" : "good")
                 : "unknown";
        return d;
    }

    private static bool Same(string a, string b)
    {
        string A = a.Replace(" ", "").ToLowerInvariant(), B = b.Replace(" ", "").ToLowerInvariant();
        return A == B || A.Contains(B) || B.Contains(A);
    }

    // Model -> (type, letters), refreshed every minute (USB drives come and go)
    private static Dictionary<string, (string kind, string letters)> WindowsInfo()
    {
        if (_winInfo != null && DateTime.Now - _winInfoAt < TimeSpan.FromMinutes(1)) return _winInfo;
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Type from the Storage WMI provider: MediaType 3 = HDD, 4 = SSD; BusType 17 = NVMe, 11 = SATA, 7 = USB
            var kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var q = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                    "SELECT FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk");
                foreach (ManagementObject o in q.Get())
                {
                    string n = o["FriendlyName"]?.ToString() ?? "";
                    int media = Convert.ToInt32(o["MediaType"] ?? 0), bus = Convert.ToInt32(o["BusType"] ?? 0);
                    string m = media == 3 ? "HDD" : media == 4 ? "SSD" : "";
                    string b = bus switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", _ => "" };
                    kinds[n] = bus == 17 ? "NVMe SSD" : bus == 7 ? "USB " + (m.Length > 0 ? m : "drive") : $"{b} {m}".Trim();
                }
            }
            catch { }

            using var drives = new ManagementObjectSearcher("SELECT DeviceID, Model FROM Win32_DiskDrive");
            foreach (ManagementObject drive in drives.Get())
            {
                string model = drive["Model"]?.ToString()?.Trim() ?? "";
                var letters = new List<string>();
                foreach (ManagementObject part in drive.GetRelated("Win32_DiskPartition"))
                    foreach (ManagementObject logical in part.GetRelated("Win32_LogicalDisk"))
                        if (logical["DeviceID"]?.ToString() is string id) letters.Add(id);
                string kind = kinds.FirstOrDefault(k => Same(k.Key, model)).Value ?? "";
                map[model] = (kind, string.Join(" ", letters.OrderBy(x => x)));
            }
        }
        catch (Exception ex) { Console.WriteLine($"[storage] Windows drive info unavailable: {ex.Message}"); }
        _winInfo = map; _winInfoAt = DateTime.Now;
        return map;
    }
}
