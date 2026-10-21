using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;
using System.Text.Json.Serialization;

// OpenRGB: the page talks to OpenRGB through the app (see OpenRGBClient)
public partial class MainForm
{
    private OpenRGBClient? _orgbClient;
    private readonly SemaphoreSlim _orgbLock = new(1, 1);
    // ── OpenRGB proxy (shared by the main window and popouts) ──────────────────
    // Paths (REST-like, from rgb.html):
    //   GET  /devices               → device list JSON
    //   PUT  /devices/{i}/leds      → [{r,g,b}, ...]
    //   PUT  /devices/{i}/mode      → {custom:true}            switch to Direct/Custom/Static
    //                                 {mode:n}                  switch to mode n
    //                                 {mode:n, r, g, b}         mode n with that colour
    //                                                           (mode-specific colours, e.g. GPU Static)
    //                                 {mode:n, brightness:0-100} hardware brightness, if the mode has one
    private async Task<(bool ok, string json)> OpenRgbProxyAsync(IncomingMessage msg)
    {
        await _orgbLock.WaitAsync();
        try
        {
            if (_orgbClient == null || !_orgbClient.IsConnected)
            {
                _orgbClient?.Dispose();
                _orgbClient = new OpenRGBClient(msg.port ?? 6742);
                await _orgbClient.ConnectAsync();
            }

            string path = msg.path ?? "/devices";
            if (path == "/devices")
                return (true, await _orgbClient.GetDevicesJsonAsync());

            var parts = path.Split('/');
            if (parts.Length < 4 || !uint.TryParse(parts[2], out uint dev) || msg.body == null)
                return (true, "{}");

            if (parts[3] == "leds")
            {
                var leds = JsonSerializer.Deserialize<List<Dictionary<string, int>>>(msg.body)!;
                await _orgbClient.SetLedsAsync(dev, leds);
            }
            else if (parts[3] == "mode" && msg.method == "PUT")
            {
                var m = JsonDocument.Parse(msg.body).RootElement;
                if (m.TryGetProperty("custom", out var cv) && cv.ValueKind == JsonValueKind.True)
                    await _orgbClient.SetCustomModeAsync(dev);
                else if (m.TryGetProperty("mode", out var mv))
                {
                    (byte, byte, byte)? color = m.TryGetProperty("r", out var rv)
                        ? ((byte)rv.GetInt32(),
                           (byte)(m.TryGetProperty("g", out var gv) ? gv.GetInt32() : 0),
                           (byte)(m.TryGetProperty("b", out var bv) ? bv.GetInt32() : 0))
                        : null;
                    int? bright = m.TryGetProperty("brightness", out var brv) ? brv.GetInt32() : null;
                    await _orgbClient.SetModeAsync(dev, mv.GetInt32(), color, bright);
                }
            }
            return (true, "{}");
        }
        catch (Exception ex)
        {
            try { _orgbClient?.Dispose(); } catch { }   // already closed; don't leak the old socket
            _orgbClient = null;
            Console.WriteLine($"[rgb] proxy error: {ex.Message}");
            return (false, ex.Message);
        }
        finally { _orgbLock.Release(); }
    }

    // Tells the RGB page when OpenRGB couldn't be started, and why (where it was found, what to do)
    private void ReportOpenRgbStart((bool ok, string message) r)
    {
        if (r.ok) return;
        RunScriptEverywhere($"typeof onOpenRgbStart==='function'&&onOpenRgbStart({JsonSerializer.Serialize(r.message)})");
    }
}
