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
    private const uint PKT_CUSTOM_MODE  = 1100;  // NET_PACKET_ID_RGBCONTROLLER_SETCUSTOMMODE (no body)
    private const uint MAX_PROTOCOL     = 5;     // highest SDK protocol this client implements

    // Mode colour types (RGBController.h)
    public const int MODE_COLORS_NONE = 0, MODE_COLORS_PER_LED = 1, MODE_COLORS_MODE_SPECIFIC = 2, MODE_COLORS_RANDOM = 3;
    private const uint MODE_FLAG_HAS_BRIGHTNESS = 1 << 4;

    // One device mode, exactly as OpenRGB describes it — kept so UpdateMode can send it back
    private sealed class ModeInfo
    {
        public string Name = "";
        public int  Value;
        public uint Flags, SpeedMin, SpeedMax, BrightMin, BrightMax, ColorsMin, ColorsMax, Speed, Brightness, Direction, ColorMode;
        public List<uint> Colors = new();
    }
    private readonly Dictionary<uint, List<ModeInfo>> _modes = new();
    private uint _proto = MAX_PROTOCOL; // negotiated: min(ours, server's)

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
            await SendAsync(0, 40, BitConverter.GetBytes(MAX_PROTOCOL));
            var verData = await RecvReplyAsync(40);
            uint serverVer = verData.Length >= 4 ? BitConverter.ToUInt32(verData, 0) : 0;
            _proto = Math.Min(MAX_PROTOCOL, serverVer);
            Console.WriteLine($"[orgb] Connected on port {_port}, server protocol v{serverVer}, using v{_proto}");
            _failedDevices.Clear(); // reset on fresh connection
        }
        catch
        {
            _proto = 0; // very old server without version negotiation
            Console.WriteLine($"[orgb] Connected on port {_port} (no version negotiation, protocol v0)");
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

    // OpenRGB also sends unsolicited packets (e.g. DEVICE_LIST_UPDATED, id 100, while it
    // is still detecting hardware). Skip anything that isn't the reply we asked for.
    private async Task<byte[]> RecvReplyAsync(uint expectedPktId)
    {
        for (int skipped = 0; ; skipped++)
        {
            var (_, pktId, data) = await RecvAsync();
            if (pktId == expectedPktId) return data;
            Console.WriteLine($"[orgb] skipped unsolicited packet id={pktId} len={data.Length}");
            if (skipped > 50) throw new IOException("no reply from OpenRGB");
        }
    }

    private async Task ReadExactAsync(byte[] buf, int count)
    {
        int got = 0;
        while (got < count)
        {
            int n = await _ns!.ReadAsync(buf.AsMemory(got, count - got));
            if (n == 0) throw new IOException("OpenRGB closed the connection");
            got += n;
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────
    public async Task<string> GetDevicesJsonAsync()
    {
        await _lock.WaitAsync();
        try
        {
            // Get count
            await SendAsync(0, PKT_COUNT, Array.Empty<byte>());
            var cntData = await RecvReplyAsync(PKT_COUNT);
            int count = (int)Read32(cntData, 0);
            Console.WriteLine($"[orgb] Device count: {count}");

            var arr = new JsonArray();
            for (int i = 0; i < count; i++)
            {
                // The 4-byte payload is the protocol version to describe the device in
                // (NetworkServer: REQUEST_CONTROLLER_DATA) — not the device index. Sending
                // the index made OpenRGB describe device 0 in protocol v0, device 1 in v1, ...
                await SendAsync((uint)i, PKT_DEV_DATA, BitConverter.GetBytes(_proto));
                var dd = await RecvReplyAsync(PKT_DEV_DATA);
                var dev = ParseDevice(i, dd);
                _modes[(uint)i] = _lastParsedModes;
                arr.Add(dev);
            }
            return arr.ToJsonString();
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Asks OpenRGB to switch the device to its software-control mode. OpenRGB picks
    /// "Direct", then "Custom", then "Static" (per-LED or mode-specific colours).
    /// Without this, devices like DRAM keep running their hardware animation and
    /// every UpdateLEDs just restarts it.
    /// </summary>
    public async Task SetCustomModeAsync(uint devIdx)
    {
        await _lock.WaitAsync();
        try
        {
            if (!IsConnected) await ConnectAsync();
            await SendAsync(devIdx, PKT_CUSTOM_MODE, Array.Empty<byte>());
            Console.WriteLine($"[orgb] SetCustomMode dev={devIdx}");
        }
        finally { _lock.Release(); }
    }

    // Reset failed device list (e.g. when OpenRGB restarts)
    public void ResetFailedDevices() => _failedDevices.Clear();

    /// <summary>
    /// Switches a device to one of its modes (UpdateMode). If a colour is given and the
    /// mode takes mode-specific colours (e.g. a GPU's "Static"), every mode colour is set
    /// to it — that is how devices without a per-LED mode get a colour.
    /// </summary>
    /// <param name="brightnessPct">0-100, mapped onto the mode's own brightness range
    /// (only for modes that have one, protocol 3+).</param>
    public async Task SetModeAsync(uint devIdx, int modeIdx, (byte r, byte g, byte b)? color = null, int? brightnessPct = null)
    {
        await _lock.WaitAsync();
        try
        {
            if (!IsConnected) await ConnectAsync();
            if (!_modes.TryGetValue(devIdx, out var modes) || modeIdx < 0 || modeIdx >= modes.Count)
            {
                Console.WriteLine($"[orgb] SetMode dev={devIdx} mode={modeIdx} skipped (unknown mode — refresh devices)");
                return;
            }
            var m = modes[modeIdx];
            if (color is { } c && m.ColorMode == MODE_COLORS_MODE_SPECIFIC)
            {
                uint packed = (uint)(c.r | (c.g << 8) | (c.b << 16)); // RGBColor = 0x00BBGGRR
                int n = Math.Max(Math.Max(1, (int)m.ColorsMin), m.Colors.Count);
                if (m.ColorsMax > 0) n = Math.Min(n, (int)m.ColorsMax);
                m.Colors = Enumerable.Repeat(packed, n).ToList();
            }
            if (brightnessPct is int bp && (m.Flags & MODE_FLAG_HAS_BRIGHTNESS) != 0 && _proto >= 3)
            {
                uint lo = Math.Min(m.BrightMin, m.BrightMax), hi = Math.Max(m.BrightMin, m.BrightMax);
                m.Brightness = lo + (uint)Math.Round((hi - lo) * Math.Clamp(bp, 0, 100) / 100.0);
            }

            var body = new List<byte>();
            body.AddRange(new byte[4]);                    // data_size, filled in below
            body.AddRange(BitConverter.GetBytes(modeIdx)); // mode_idx (int)
            WriteMode(body, m, _proto);
            var pkt = body.ToArray();
            BitConverter.TryWriteBytes(pkt.AsSpan(0, 4), (uint)pkt.Length);
            await SendAsync(devIdx, PKT_UPDATE_MODE, pkt);
            Console.WriteLine($"[orgb] SetMode dev={devIdx} mode={modeIdx} '{m.Name}' colorMode={m.ColorMode} colors={m.Colors.Count}");
        }
        finally { _lock.Release(); }
    }

    // Mode description, same layout OpenRGB sends in device data (protocol < 6)
    private static void WriteMode(List<byte> o, ModeInfo m, uint proto)
    {
        var name = Encoding.UTF8.GetBytes(m.Name);
        o.AddRange(BitConverter.GetBytes((ushort)(name.Length + 1)));
        o.AddRange(name); o.Add(0);
        o.AddRange(BitConverter.GetBytes(m.Value));
        o.AddRange(BitConverter.GetBytes(m.Flags));
        o.AddRange(BitConverter.GetBytes(m.SpeedMin));
        o.AddRange(BitConverter.GetBytes(m.SpeedMax));
        if (proto >= 3) { o.AddRange(BitConverter.GetBytes(m.BrightMin)); o.AddRange(BitConverter.GetBytes(m.BrightMax)); }
        o.AddRange(BitConverter.GetBytes(m.ColorsMin));
        o.AddRange(BitConverter.GetBytes(m.ColorsMax));
        o.AddRange(BitConverter.GetBytes(m.Speed));
        if (proto >= 3) o.AddRange(BitConverter.GetBytes(m.Brightness));
        o.AddRange(BitConverter.GetBytes(m.Direction));
        o.AddRange(BitConverter.GetBytes(m.ColorMode));
        o.AddRange(BitConverter.GetBytes((ushort)m.Colors.Count));
        foreach (var c in m.Colors) o.AddRange(BitConverter.GetBytes(c));
    }

    public async Task SetLedsAsync(uint devIdx, List<Dictionary<string, int>> leds)
    {
        await _lock.WaitAsync();
        try
        {
            if (!IsConnected) { await ConnectAsync(); }
            if (leds.Count == 0) { Console.WriteLine($"[orgb] SetLEDs dev={devIdx} skipped (0 LEDs)"); return; }
            int numLeds = leds.Count;

            // Skip devices that have failed repeatedly
            if (_failedDevices.Contains(devIdx))
            {
                Console.WriteLine($"[orgb] SetLEDs dev={devIdx} skipped (marked failed)");
                return; // the finally below releases the lock
            }

            // UpdateLEDs body (OpenRGB NetworkServer / RGBController::SetColorDescription):
            //   uint32 data_size   — total body size, including these 4 bytes
            //   uint16 num_colors
            //   RGBColor[num]      — 4 bytes each (R, G, B, pad)
            // OpenRGB checks data_size against the packet size; without it the packet
            // is malformed and OpenRGB drops the connection ("aborted by the host").
            uint bodySize = (uint)(4 + 2 + numLeds * 4);
            var pkt = new List<byte>((int)bodySize);
            pkt.AddRange(BitConverter.GetBytes(bodySize));
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
    // Follows OpenRGB's RGBController::GetDeviceDescriptionData for protocol v0-v5:
    //   data_size, type, name, [vendor v1+], description, version, serial, location,
    //   num_modes(u16), active_mode(i32), modes[], num_zones(u16), zones[],
    //   num_leds(u16), leds[], num_colors(u16), colors[],
    //   [v5+: led display names, device flags]
    private List<ModeInfo> _lastParsedModes = new();

    private JsonObject ParseDevice(int idx, byte[] d)
    {
        string name = $"Device {idx}", vendor = "", serial = "", location = "";
        int devType = 0, activeMode = 0, zoneLeds = 0, numLeds = 0;
        var modes = new List<ModeInfo>();
        var colors = new JsonArray();
        bool complete = false;

        try
        {
            int p = 0;
            Read32(d, ref p);                                   // data_size
            devType = (int)Read32(d, ref p);                    // device_type
            name    = ReadStr(d, ref p);
            if (_proto >= 1) vendor = ReadStr(d, ref p);
            ReadStr(d, ref p);                                  // description
            ReadStr(d, ref p);                                  // version
            serial   = ReadStr(d, ref p);
            location = ReadStr(d, ref p);                       // e.g. "HID: \\?\hid#vid_1b1c..." — stable per port

            int numModes = ReadU16(d, ref p);
            activeMode   = (int)Read32(d, ref p);
            for (int m = 0; m < numModes; m++)
            {
                var mi = new ModeInfo { Name = ReadStr(d, ref p) };
                mi.Value     = (int)Read32(d, ref p);
                mi.Flags     = Read32(d, ref p);
                mi.SpeedMin  = Read32(d, ref p);
                mi.SpeedMax  = Read32(d, ref p);
                if (_proto >= 3) { mi.BrightMin = Read32(d, ref p); mi.BrightMax = Read32(d, ref p); }
                mi.ColorsMin = Read32(d, ref p);
                mi.ColorsMax = Read32(d, ref p);
                mi.Speed     = Read32(d, ref p);
                if (_proto >= 3) mi.Brightness = Read32(d, ref p);
                mi.Direction = Read32(d, ref p);
                mi.ColorMode = Read32(d, ref p);
                int nc = ReadU16(d, ref p);
                for (int c = 0; c < nc; c++) mi.Colors.Add(Read32(d, ref p));
                modes.Add(mi);
            }

            int numZones = ReadU16(d, ref p);
            for (int z = 0; z < numZones; z++)
            {
                ReadStr(d, ref p);                              // zone name
                Read32(d, ref p);                               // zone type
                Read32(d, ref p);                               // leds_min
                Read32(d, ref p);                               // leds_max
                zoneLeds += (int)Read32(d, ref p);              // leds_count
                int matrixBytes = ReadU16(d, ref p);
                p += matrixBytes;                               // height, width, map
                if (_proto >= 4)
                {
                    int segs = ReadU16(d, ref p);
                    for (int sg = 0; sg < segs; sg++)
                    { ReadStr(d, ref p); Read32(d, ref p); Read32(d, ref p); Read32(d, ref p); }
                }
                if (_proto >= 5) Read32(d, ref p);              // zone flags
            }

            numLeds = ReadU16(d, ref p);
            for (int l = 0; l < numLeds; l++) { ReadStr(d, ref p); Read32(d, ref p); } // name, value

            int numColors = ReadU16(d, ref p);
            for (int c = 0; c < numColors; c++)
            {
                var col = new JsonObject { ["r"] = (int)d[p], ["g"] = (int)d[p + 1], ["b"] = (int)d[p + 2] };
                colors.Add(col);
                p += 4;
            }
            complete = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[orgb] Device {idx} ({name}) parse error: {ex.Message} — " +
                              $"{d.Length} bytes, protocol v{_proto}, first bytes: " +
                              BitConverter.ToString(d, 0, Math.Min(64, d.Length)));
        }

        int effectiveLeds = colors.Count > 0 ? colors.Count : Math.Max(numLeds, zoneLeds);
        _lastParsedModes = modes;

        var modeArr = new JsonArray();
        foreach (var m in modes)
            modeArr.Add(new JsonObject
            {
                ["name"] = m.Name, ["flags"] = (int)m.Flags, ["color_mode"] = (int)m.ColorMode,
                ["colors_min"] = (int)m.ColorsMin, ["colors_max"] = (int)m.ColorsMax,
                ["has_brightness"] = (m.Flags & MODE_FLAG_HAS_BRIGHTNESS) != 0 && _proto >= 3
            });
        bool perLed      = modes.Any(m => m.ColorMode == MODE_COLORS_PER_LED);
        bool modeColored = modes.Any(m => m.ColorMode == MODE_COLORS_MODE_SPECIFIC);

        Console.WriteLine($"[orgb] Device {idx}: {name} type={devType} leds={effectiveLeds} modes={modes.Count} " +
                          $"(perLed={perLed}, modeColors={modeColored}{(complete ? "" : ", PARTIAL")}) " +
                          $"[{string.Join(", ", modes.Select(m => $"{m.Name}:{m.ColorMode}"))}]");

        return new JsonObject
        {
            ["name"]        = name.Length > 0 ? name : $"Device {idx}",
            ["vendor"]      = vendor,
            ["serial"]      = serial,
            ["location"]    = location,
            ["type"]        = devType,
            ["active_mode"] = activeMode,
            ["modes"]       = modeArr,
            ["per_led"]     = perLed,
            ["mode_colors"] = modeColored,
            ["leds"]        = effectiveLeds,
            ["num_leds"]    = effectiveLeds,
            ["colors"]      = colors
        };
    }

    // ── Binary helpers ────────────────────────────────────────────────────────
    private static uint   Read32 (byte[] d, int off)       => BitConverter.ToUInt32(d, off);
    private static uint   Read32 (byte[] d, ref int p)     { var v=BitConverter.ToUInt32(d,p); p+=4; return v; }
    private static int    ReadU16(byte[] d, ref int p)     { var v=BitConverter.ToUInt16(d,p); p+=2; return v; }
    private static void   Write32(byte[] b, int off, uint v) => BitConverter.TryWriteBytes(b.AsSpan(off, 4), v);

    private static string ReadStr(byte[] d, ref int p)
    {
        int len = BitConverter.ToUInt16(d, p); p += 2;       // throws past the end
        if (len == 0) return "";
        if (p + len > d.Length) throw new IndexOutOfRangeException("string past end of data");
        var s = Encoding.UTF8.GetString(d, p, len - 1);   // exclude null terminator
        p += len;
        return s;
    }

    public void Dispose()
    {
        _ns?.Dispose();
        _tcp?.Dispose();
    }
}
