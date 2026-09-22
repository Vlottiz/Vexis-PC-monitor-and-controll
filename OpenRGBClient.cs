using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// Implements the OpenRGB SDK binary protocol.
/// Protocol: https://gitlab.com/CalcProgrammer1/OpenRGB/-/wikis/OpenRGB-SDK-Documentation
/// Packet: magic(4) | device_idx(4) | packet_id(4) | data_size(4) | data(N)
/// </summary>
public class OpenRGBClient : IDisposable
{
    // "ORGB" = 0x4F,0x52,0x47,0x42 → little-endian uint32 = 0x4247524F
    private const uint MAGIC            = 0x4247524F;
    private const uint PKT_COUNT        = 0;
    private const uint PKT_DEV_DATA     = 1;
    private const uint PKT_UPDATE_LEDS  = 1050;  // NET_PACKET_ID_RGBCONTROLLER_UPDATELEDS
    private const uint PKT_UPDATE_MODE  = 1101;  // NET_PACKET_ID_RGBCONTROLLER_UPDATEMODE

    private readonly int     _port;
    private TcpClient?       _tcp;
    private NetworkStream?   _ns;
    private readonly SemaphoreSlim _lock = new(1, 1);
    // Track devices that consistently fail so we stop hammering them
    private readonly HashSet<uint> _failedDevices = new();

    public bool IsConnected => _tcp?.Connected == true && _tcp?.Client?.Connected == true && _ns != null;

    public OpenRGBClient(int port = 6742) { _port = port; }

    public async Task ConnectAsync()
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync("127.0.0.1", _port);
        _tcp.ReceiveTimeout = 8000;
        _tcp.SendTimeout    = 5000;
        _ns = _tcp.GetStream();

        // Negotiate protocol version (required for OpenRGB 1.0+)
        // Send our supported version (3), server replies with agreed version
        try
        {
            await SendAsync(0, 40, BitConverter.GetBytes((uint)5));
            var (_, _, verData) = await RecvAsync();
            uint agreedVer = verData.Length >= 4 ? BitConverter.ToUInt32(verData, 0) : 0;
            Console.WriteLine($"[orgb] Connected on port {_port}, protocol v{agreedVer}");
            _failedDevices.Clear(); // reset on fresh connection
        }
        catch
        {
            Console.WriteLine($"[orgb] Connected on port {_port} (no version negotiation)");
        }
    }

    // ── Send / Receive ────────────────────────────────────────────────────────
    private async Task SendAsync(uint devIdx, uint pktId, byte[] data)
    {
        var buf = new byte[16 + data.Length];
        // Write magic "ORGB" = [0x4F, 0x52, 0x47, 0x42] explicitly
        buf[0] = 0x4F; buf[1] = 0x52; buf[2] = 0x47; buf[3] = 0x42;
        // Device index (little-endian uint32)
        buf[4]  = (byte)( devIdx        & 0xFF);
        buf[5]  = (byte)((devIdx >>  8) & 0xFF);
        buf[6]  = (byte)((devIdx >> 16) & 0xFF);
        buf[7]  = (byte)((devIdx >> 24) & 0xFF);
        // Packet ID (little-endian uint32)
        buf[8]  = (byte)( pktId        & 0xFF);
        buf[9]  = (byte)((pktId >>  8) & 0xFF);
        buf[10] = (byte)((pktId >> 16) & 0xFF);
        buf[11] = (byte)((pktId >> 24) & 0xFF);
        // Data length (little-endian uint32)
        uint dl = (uint)data.Length;
        buf[12] = (byte)( dl        & 0xFF);
        buf[13] = (byte)((dl >>  8) & 0xFF);
        buf[14] = (byte)((dl >> 16) & 0xFF);
        buf[15] = (byte)((dl >> 24) & 0xFF);
        data.CopyTo(buf, 16);
        Console.WriteLine($"[orgb] SEND hdr: devIdx={devIdx} pktId={pktId} dataLen={dl} | bytes: {string.Join(' ', buf.Take(16).Select(b => b.ToString("X2")))}");
        await _ns!.WriteAsync(buf);
    }

    private async Task<(uint devIdx, uint pktId, byte[] data)> RecvAsync()
    {
        var hdr = new byte[16];
        await ReadExactAsync(hdr, 16);
        uint magic = Read32(hdr, 0);
        if (magic != MAGIC) throw new Exception($"Bad magic: 0x{magic:X8}");
        uint devIdx = Read32(hdr, 4);
        uint pktId  = Read32(hdr, 8);
        uint len    = Read32(hdr, 12);
        var data = new byte[len];
        if (len > 0) await ReadExactAsync(data, (int)len);
        return (devIdx, pktId, data);
    }

    private async Task ReadExactAsync(byte[] buf, int count)
    {
        int got = 0;
        while (got < count)
            got += await _ns!.ReadAsync(buf.AsMemory(got, count - got));
    }

    // ── Public API ────────────────────────────────────────────────────────────
    public async Task<string> GetDevicesJsonAsync()
    {
        await _lock.WaitAsync();
        try
        {
            // Get count
            await SendAsync(0, PKT_COUNT, Array.Empty<byte>());
            var (_, _, cntData) = await RecvAsync();
            int count = (int)Read32(cntData, 0);
            Console.WriteLine($"[orgb] Device count: {count}");

            var arr = new JsonArray();
            for (int i = 0; i < count; i++)
            {
                await SendAsync((uint)i, PKT_DEV_DATA, BitConverter.GetBytes((uint)i));
                var (_, _, dd) = await RecvAsync();
                var dev = ParseDevice(i, dd);
                arr.Add(dev);
            }
            return arr.ToJsonString();
        }
        finally { _lock.Release(); }
    }

    public Task SetCustomModeAsync(uint devIdx)
    {
        Console.WriteLine($"[orgb] SetCustomMode dev={devIdx} (no-op)");
        return Task.CompletedTask;
    }

    // Reset failed device list (e.g. when OpenRGB restarts)
    public void ResetFailedDevices() => _failedDevices.Clear();

    public async Task SetModeAsync(uint devIdx, int modeIdx)
    {
        await _lock.WaitAsync();
        try
        {
            // UpdateMode packet: uint32 data_size, then mode data
            // Simplified: just send mode index as uint32
            var pkt = new List<byte>();
            pkt.AddRange(BitConverter.GetBytes((uint)modeIdx));
            await SendAsync(devIdx, PKT_UPDATE_MODE, pkt.ToArray());
            Console.WriteLine($"[orgb] SetMode dev={devIdx} mode={modeIdx}");
        }
        finally { _lock.Release(); }
    }

    public async Task SetLedsAsync(uint devIdx, List<Dictionary<string, int>> leds)
    {
        await _lock.WaitAsync();
        try
        {
            if (!IsConnected) { await ConnectAsync(); }
            if (leds.Count == 0) { Console.WriteLine($"[orgb] SetLEDs dev={devIdx} skipped (0 LEDs)"); return; }
            // OpenRGB UpdateLEDs format: no count prefix, raw RGBW colors only
            // OpenRGB computes count from packet size: count = packet_size / 4
            int numLeds = leds.Count;

            // Skip devices that have failed repeatedly
            if (_failedDevices.Contains(devIdx))
            {
                Console.WriteLine($"[orgb] SetLEDs dev={devIdx} skipped (marked failed)");
                _lock.Release();
                return;
            }

            var pkt = new List<byte>();
            // UpdateLEDs body: uint16 num_leds + RGBColor[N] (4 bytes each: RGBW)
            // Validated: packet_size == 2 + num_leds*4
            pkt.Add((byte)( numLeds       & 0xFF));
            pkt.Add((byte)((numLeds >> 8) & 0xFF));
            foreach (var led in leds)
            {
                pkt.Add((byte)led.GetValueOrDefault("r"));
                pkt.Add((byte)led.GetValueOrDefault("g"));
                pkt.Add((byte)led.GetValueOrDefault("b"));
                pkt.Add(0); // W/padding
            }
            int dataSize = pkt.Count;
            Console.WriteLine($"[orgb] SetLEDs dev={devIdx} count={numLeds} dataSize={dataSize} pktBytes={dataSize}");
            await SendAsync(devIdx, PKT_UPDATE_LEDS, pkt.ToArray());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[orgb] SetLEDs dev={devIdx} failed: {ex.Message}");
            bool streamDead = _ns == null || !(_tcp?.Connected ?? false);
            if (streamDead)
            {
                Console.WriteLine("[orgb] Stream dead — reconnecting");
                try { _ns?.Dispose(); _tcp?.Dispose(); } catch {}
                _ns = null; _tcp = null;
                try { await Task.Delay(800); await ConnectAsync(); await Task.Delay(200); }
                catch (Exception rex) { Console.WriteLine($"[orgb] Reconnect failed: {rex.Message}"); }
            }
            else
            {
                // Stream still alive but this device rejected the packet — mark it failed
                Console.WriteLine($"[orgb] dev={devIdx} rejected by OpenRGB — marking as failed, will skip");
                _failedDevices.Add(devIdx);
            }
        }
        finally { _lock.Release(); }
    }

    // ── Device data parser ────────────────────────────────────────────────────
    // Strategy: do a best-effort forward parse to extract name/vendor/type/modes,
    // but always fall back to backward scan for LED count — it's the most reliable
    // approach across OpenRGB protocol versions (v3, v4, v5+).
    // Protocol v5 added fields to the mode struct; rather than versioning the parser
    // we guard every read and use the backward scan as the authoritative LED source.
    private static JsonObject ParseDevice(int idx, byte[] d)
    {
        // ── Always do the backward scan first — it never throws ───────────────
        // The colors section is always the LAST field: [uint16 N][RGBColor*N]
        // Scan from largest possible N down, find the uint16 that equals N.
        int scannedN = 0;
        {
            int maxPossible = Math.Min(2048, (d.Length - 4) / 4);
            for (int N = maxPossible; N >= 4; N--)
            {
                int scanPos = d.Length - 2 - N * 4;
                if (scanPos < 4 || scanPos + 1 >= d.Length) continue;
                if (BitConverter.ToUInt16(d, scanPos) != (ushort)N) continue;
                // Extra check: byte before should not form a matching N+1
                bool ok = true;
                if (scanPos >= 6 && BitConverter.ToUInt16(d, scanPos - 4) == (ushort)(N + 1))
                    ok = false;
                if (ok) { scannedN = N; break; }
            }
        }

        // ── Forward parse — wrapped in try so a bad device can never crash ────
        string name = $"Device {idx}";
        string vendor = "";
        int devType = 0, activeMode = 0;
        var modes = new JsonArray();
        int forwardLeds = 0;

        try
        {
            int p = 0;
            Read32(d, ref p);                          // data_size
            devType    = (int)Read32(d, ref p);        // device_type
            name       = ReadStr(d, ref p);
            vendor     = ReadStr(d, ref p);
            ReadStr(d, ref p);                         // description
            ReadStr(d, ref p);                         // version
            ReadStr(d, ref p);                         // serial
            ReadStr(d, ref p);                         // location
            activeMode = ReadU16(d, ref p);
            int numModes = ReadU16(d, ref p);

            for (int m = 0; m < numModes && p + 2 < d.Length; m++)
            {
                string modeName = ReadStr(d, ref p);
                // Mode struct: value(4) flags(4) speed_min(4) speed_max(4)
                //   brightness_min(4) brightness_max(4) colors_min(4) colors_max(4)
                //   speed(4) brightness(4) direction(4) color_mode(4) = 48 bytes fixed
                // Protocol v5+ may add more fields but we skip via color count anyway
                if (p + 48 > d.Length) break;
                Read32(d, ref p); // value
                uint flags = Read32(d, ref p);
                Read32(d, ref p); // speed_min
                Read32(d, ref p); // speed_max
                Read32(d, ref p); // brightness_min
                Read32(d, ref p); // brightness_max
                Read32(d, ref p); // colors_min
                Read32(d, ref p); // colors_max
                Read32(d, ref p); // speed
                Read32(d, ref p); // brightness
                Read32(d, ref p); // direction
                Read32(d, ref p); // color_mode
                if (p + 2 > d.Length) break;
                int numModeColors = ReadU16(d, ref p);
                if (numModeColors < 0 || numModeColors > 2048) break; // sanity
                for (int c = 0; c < numModeColors && p + 4 <= d.Length; c++) p += 4;
                var mo = new JsonObject(); mo["name"] = modeName; mo["flags"] = (int)flags;
                modes.Add(mo);
            }

            // Parse zones to get a forward LED count as fallback
            if (p + 2 <= d.Length)
            {
                int numZones = ReadU16(d, ref p);
                for (int z = 0; z < numZones && p + 2 < d.Length; z++)
                {
                    ReadStr(d, ref p);               // zone name
                    if (p + 18 > d.Length) break;
                    Read32(d, ref p);                // zone_type
                    Read32(d, ref p);                // leds_min
                    Read32(d, ref p);                // leds_max
                    forwardLeds += (int)Read32(d, ref p); // leds_count
                    if (p + 2 > d.Length) break;
                    int matrixBytes = ReadU16(d, ref p);
                    if (matrixBytes > 0 && p + matrixBytes <= d.Length) p += matrixBytes;
                }
            }
        }
        catch (Exception ex)
        {
            // Forward parse failed partway — that's fine, we still have scannedN
            Console.WriteLine($"[orgb] Device {idx} forward parse partial: {ex.Message} — using backward scan ({scannedN} LEDs)");
        }

        // ── Build colors from backward scan (most reliable) ───────────────────
        var colors = new JsonArray();
        if (scannedN > 0)
        {
            int colStart = d.Length - scannedN * 4;
            for (int c = 0; c < scannedN && colStart + c*4 + 3 < d.Length; c++)
            {
                byte r = d[colStart + c*4]; byte g = d[colStart + c*4 + 1]; byte b = d[colStart + c*4 + 2];
                var col = new JsonObject(); col["r"]=(int)r; col["g"]=(int)g; col["b"]=(int)b;
                colors.Add(col);
            }
        }

        int effectiveLeds = scannedN > 0 ? scannedN : Math.Max(forwardLeds, colors.Count);
        Console.WriteLine($"[orgb] Device {idx}: {name}, leds={effectiveLeds} (scan={scannedN} fwd={forwardLeds})");

        var dev = new JsonObject();
        dev["name"]        = name.Length > 0 ? name : $"Device {idx}";
        dev["vendor"]      = vendor;
        dev["type"]        = devType;
        dev["active_mode"] = activeMode;
        dev["modes"]       = modes;
        dev["leds"]        = effectiveLeds;
        dev["num_leds"]    = effectiveLeds;
        dev["colors"]      = colors;
        return dev;
    }

    // ── Binary helpers ────────────────────────────────────────────────────────
    private static uint   Read32 (byte[] d, int off)       => BitConverter.ToUInt32(d, off);
    private static uint   Read32 (byte[] d, ref int p)     { var v=BitConverter.ToUInt32(d,p); p+=4; return v; }
    private static int    ReadU16(byte[] d, ref int p)     { var v=BitConverter.ToUInt16(d,p); p+=2; return v; }
    private static void   Write32(byte[] b, int off, uint v) => BitConverter.TryWriteBytes(b.AsSpan(off, 4), v);

    private static string ReadStr(byte[] d, ref int p)
    {
        if (p + 2 > d.Length) return "";
        int len = BitConverter.ToUInt16(d, p); p += 2;
        if (len <= 0 || p + len > d.Length) return "";
        var s = Encoding.UTF8.GetString(d, p, len - 1); // exclude null terminator
        p += len;
        return s;
    }

    public void Dispose()
    {
        _ns?.Dispose();
        _tcp?.Dispose();
    }
}
