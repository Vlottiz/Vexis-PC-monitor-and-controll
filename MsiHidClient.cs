using System.Runtime.InteropServices;

/// <summary>
/// MSI X870E TOMAHAWK WIFI RGB controller via hidapi.dll.
/// HIDAPI is the same library OpenRGB and SignalRGB use — proven to work
/// with this device where Windows raw HidD_SetFeature does not.
///
/// SETUP: Place hidapi.dll alongside PCMonitor.exe
/// Download: https://github.com/libusb/hidapi/releases
///           → Windows x64 zip → hidapi.dll
/// Add to installer.nsi: File "hidapi.dll"
/// </summary>
public class MsiHidClient : IDisposable
{
    // ── Device identity ───────────────────────────────────────────────────────
    private const ushort VID       = 0x0DB0;  // MSI
    private const ushort PID       = 0x0076;  // X870E TOMAHAWK WIFI RGB controller
    private const int    SETUP_LEN = 290;     // initialization packet
    private const int    COLOR_LEN = 761;     // per-zone color packet
    private const int    MAX_LEDS  = 240;     // LEDs per zone

    // Zones from MSIMysticLight761Controller.cpp zone_set1
    public static readonly (string Name, byte ReportId, byte ZoneIdx)[] ZONES =
    {
        ("JARGB 1", 0x04, 0x00),  // Addressable RGB header 1
        ("JARGB 2", 0x04, 0x01),  // Addressable RGB header 2
        ("JARGB 3", 0x04, 0x02),  // Addressable RGB header 3
        ("JAF",     0x08, 0x00),  // ARGB fan header
    };

    private IntPtr _dev = IntPtr.Zero;
    public  bool   IsOpen => _dev != IntPtr.Zero;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    public bool Open()
    {
        Close();
        try
        {
            hid_init();
            // IntPtr.Zero serial = open first matching device
            _dev = hid_open(VID, PID, IntPtr.Zero);
            if (_dev == IntPtr.Zero)
            {
                Console.WriteLine($"[msihid] Device VID={VID:X4} PID={PID:X4} not found — is it plugged in?");
                return false;
            }
            Console.WriteLine($"[msihid] Opened VID={VID:X4} PID={PID:X4}");
            return true;
        }
        catch (DllNotFoundException)
        {
            Console.WriteLine("[msihid] hidapi.dll missing — download from github.com/libusb/hidapi/releases and place next to PCMonitor.exe");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[msihid] Open error: {ex.Message}");
            return false;
        }
    }

    public void Close()
    {
        if (IsOpen) { hid_close(_dev); _dev = IntPtr.Zero; }
    }

    public void Dispose() { Close(); try { hid_exit(); } catch { } }

    // ── Initialize ────────────────────────────────────────────────────────────
    /// <summary>
    /// Send the 290-byte setup packet that tells the controller which zones
    /// are active. Must be called once after Open() before sending colors.
    /// </summary>
    public bool Initialize()
    {
        if (!IsOpen) return false;
        byte[] pkt = BuildSetupPacket();
        int res = hid_send_feature_report(_dev, pkt, (UIntPtr)pkt.Length);
        Console.WriteLine($"[msihid] Initialize → {(res < 0 ? $"FAIL {GetError()}" : $"OK ({res} bytes)")}");
        return res >= 0;
    }

    // ── Color control ─────────────────────────────────────────────────────────
    /// <summary>Set a single zone to a solid color.</summary>
    public bool SetZoneColor(string zoneName, byte r, byte g, byte b)
    {
        if (!IsOpen) return false;
        var zone = ZONES.FirstOrDefault(z => z.Name == zoneName);
        if (zone == default)
        {
            Console.WriteLine($"[msihid] Unknown zone '{zoneName}'");
            return false;
        }
        byte[] pkt = BuildColorPacket(zone.ReportId, zone.ZoneIdx, r, g, b); // ReportId=hdr0, ZoneIdx=hdr1

        // Report ID 0x51 is the correct HID feature report ID for 761-byte color packets.
        // (We were previously sending 0x04/0x08 which don't exist — that caused INVALID_PARAMETER)
        int res = hid_send_feature_report(_dev, pkt, (UIntPtr)pkt.Length);
        Console.WriteLine($"[msihid] SetZone {zoneName} #{r:X2}{g:X2}{b:X2} → " +
                          (res < 0 ? $"FAIL ({GetError()})" : $"OK ({res} bytes)"));
        return res >= 0;
    }

    /// <summary>Set every zone to the same color.</summary>
    public bool SetAllZones(byte r, byte g, byte b)
    {
        bool ok = true;
        foreach (var z in ZONES)
            ok &= SetZoneColor(z.Name, r, g, b);
        return ok;
    }

    // ── Packet builders ───────────────────────────────────────────────────────
    private static byte[] BuildSetupPacket()
    {
        // 290-byte initialization packet.
        // Layout from MSIMysticLight761Controller.cpp (rom4ster, Jun 2025).
        // Report ID 0x50 + 17 rows × 16 bytes + 1 terminator byte.
        byte[] pkt = new byte[SETUP_LEN];
        pkt[0] = 0x50; // HID feature report ID for setup

        // Active zone row — mode 0x09, RGB color pair, flags, speed, brightness
        byte[] active = { 0x09, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00,
                          0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x03, 0x15, 0x78 };
        // Inactive zone row
        byte[] inact  = { 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                          0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x94, 0x1E };

        // Rows 0-3: JARGB1, JARGB2, JARGB3, JAF — all active on this board
        for (int row = 0; row < 4; row++)
            Array.Copy(active, 0, pkt, 1 + row * 16, 16);

        // Rows 4-16: JPIPE1-5, JRGB1-2, Onboard LEDs — inactive
        for (int row = 4; row < 17; row++)
            Array.Copy(inact,  0, pkt, 1 + row * 16, 16);

        // Row 17: select-all sentinel
        byte[] sel = { 0x09, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00,
                       0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x03, 0x95, 0x1E };
        int off = 1 + 17 * 16;
        if (off + 16 <= pkt.Length) Array.Copy(sel, 0, pkt, off, 16);

        pkt[SETUP_LEN - 1] = 0x00; // terminator
        return pkt;
    }

    private static byte[] BuildColorPacket(byte hdr0, byte hdr1, byte r, byte g, byte b)
    {
        // 761-byte color packet — EXACT layout from FeaturePacket_PerLED_761 in MSIMysticLightCommon.h:
        // [0]  0x51       — HID feature report ID (ALWAYS 0x51, not 0x04/0x08!)
        // [1]  0x09       — fixed1
        // [2]  hdr0       — zone type: 0x04=JARGB, 0x08=JAF
        // [3]  hdr1       — zone index: 0=JARGB1, 1=JARGB2, 2=JARGB3, 0=JAF
        // [4]  0x00       — fixed2
        // [5]  0x00       — fixed3
        // [6]  240        — hdr2: LED count (NUM_LEDS_761/3 = 240)
        // [7..726]        — 720 bytes RGB data (NUM_LEDS_761=720, i.e. 240 LEDs × 3 bytes)
        // [727..760]      — padding zeros to reach 761 bytes
        byte[] pkt = new byte[COLOR_LEN];
        pkt[0] = 0x51;          // HID report ID
        pkt[1] = 0x09;          // fixed1
        pkt[2] = hdr0;          // zone type (0x04 JARGB / 0x08 JAF)
        pkt[3] = hdr1;          // zone index
        pkt[4] = 0x00;          // fixed2
        pkt[5] = 0x00;          // fixed3
        pkt[6] = (byte)MAX_LEDS; // LED count = 240

        // Fill all 240 LED slots (720 bytes) with the solid color
        for (int i = 0; i < MAX_LEDS; i++)
        {
            int b_ = 7 + i * 3;
            if (b_ + 2 >= COLOR_LEN) break;
            pkt[b_ + 0] = r;
            pkt[b_ + 1] = g;
            pkt[b_ + 2] = b;
        }
        return pkt;
    }

    // ── Error helper ──────────────────────────────────────────────────────────
    private string GetError()
    {
        try
        {
            IntPtr p = hid_error(_dev);
            return p == IntPtr.Zero ? "(no error string)" : Marshal.PtrToStringUni(p) ?? "(null)";
        }
        catch { return "(error unavailable)"; }
    }

    // ── HIDAPI P/Invoke ───────────────────────────────────────────────────────
    // hidapi.dll — MIT License — https://github.com/libusb/hidapi
    // CallingConvention.Cdecl matches hidapi's export convention on all platforms.
    private const string DLL = "hidapi";

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern int hid_init();

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern int hid_exit();

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr hid_open(ushort vendorId, ushort productId, IntPtr serialNumber);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern int hid_send_feature_report(IntPtr dev, byte[] data, UIntPtr length);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern int hid_write(IntPtr dev, byte[] data, UIntPtr length);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr hid_error(IntPtr dev);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern void hid_close(IntPtr dev);
}
