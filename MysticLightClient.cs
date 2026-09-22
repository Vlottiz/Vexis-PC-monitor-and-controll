using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// Client for MSI Center's Mystic Light local HTTP SDK API.
/// MSI Center runs a REST server on localhost at a dynamic port.
/// We auto-discover it by probing localhost ports for the /sdk/version endpoint.
/// SDK must be enabled in MSI Center Settings → Gaming → Mystic Light → Enable SDK.
/// </summary>
public class MysticLightClient : IDisposable
{
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };

    // Candidate ports: known MSI Center ports + the ones from the user's netstat
    private static readonly int[] CANDIDATE_PORTS = { 14630, 32683, 33683, 9595, 33333, 35151, 7575 };

    public int    Port      { get; private set; } = -1;
    public string Version   { get; private set; } = "";
    public bool   IsEnabled { get; private set; } = false;

    // Current zone colors for keep-alive resend (MSI clears on timeout)
    private readonly Dictionary<string, (byte r, byte g, byte b)> _zoneColors = new();
    private System.Threading.Timer? _keepAlive;

    // Known X870E zones from the 761 driver (zone_set1)
    public static readonly string[] ZONES = { "JARGB 1", "JARGB 2", "JARGB 3", "JAF" };

    /// <summary>Scan localhost ports to find MSI Center's SDK server.</summary>
    public async Task<bool> DiscoverAsync()
    {
        foreach (int port in CANDIDATE_PORTS)
        {
            try
            {
                string url = $"http://127.0.0.1:{port}/sdk/version";
                var resp = await _http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) continue;
                string body = await resp.Content.ReadAsStringAsync();
                var json = System.Text.Json.JsonDocument.Parse(body).RootElement;
                if (json.TryGetProperty("version", out var ver))
                {
                    Port    = port;
                    Version = ver.GetString() ?? "";
                    Console.WriteLine($"[msi] Found MSI Center SDK on port {port}, v{Version}");
                    await RefreshSdkEnabledAsync();
                    return true;
                }
            }
            catch { /* not this port */ }
        }
        Console.WriteLine("[msi] MSI Center SDK not found on any candidate port");
        return false;
    }

    private async Task RefreshSdkEnabledAsync()
    {
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{Port}/sdk/setting");
            string body = await resp.Content.ReadAsStringAsync();
            var json = System.Text.Json.JsonDocument.Parse(body).RootElement;
            IsEnabled = json.TryGetProperty("isenablesdk", out var en) && en.GetBoolean();
            Console.WriteLine($"[msi] SDK enabled: {IsEnabled}");
        }
        catch { }
    }

    /// <summary>Enable the Mystic Light SDK (required before setting colors).</summary>
    public async Task<bool> EnableSdkAsync()
    {
        if (Port < 0) return false;
        try
        {
            var content = new StringContent("{\"isenablesdk\":true}", Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync($"http://127.0.0.1:{Port}/sdk/setting", content);
            IsEnabled = resp.IsSuccessStatusCode;
            Console.WriteLine($"[msi] EnableSDK → {resp.StatusCode}");
            return IsEnabled;
        }
        catch (Exception ex) { Console.WriteLine($"[msi] EnableSDK error: {ex.Message}"); return false; }
    }

    /// <summary>Get current SDK command/state (useful for reading current color).</summary>
    public async Task<string> GetCommandAsync()
    {
        if (Port < 0) return "{}";
        try
        {
            var resp = await _http.GetAsync($"http://127.0.0.1:{Port}/sdk/command");
            return await resp.Content.ReadAsStringAsync();
        }
        catch { return "{}"; }
    }

    /// <summary>Set a zone to a solid color.</summary>
    public async Task<bool> SetZoneColorAsync(string zone, byte r, byte g, byte b)
    {
        if (Port < 0 || !IsEnabled) return false;
        try
        {
            // MSI SDK 2.0 command format (zone-indexed static color).
            // Zones map: JARGB 1→index 0, JARGB 2→1, JARGB 3→2, JAF→3
            int zoneIdx = Array.IndexOf(ZONES, zone);
            if (zoneIdx < 0) zoneIdx = 0;

            string hex = $"#{r:X2}{g:X2}{b:X2}";
            // Command payload — sends a static color to one zone.
            // Field names from SDK log inspection + MSI documentation.
            var payload = new JsonObject
            {
                ["smode_select"] = "User",
                ["iAreaIndex"]   = zoneIdx,
                ["color"]        = hex,
                ["ibrightness"]  = 100,
            };
            string json = payload.ToJsonString();
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync($"http://127.0.0.1:{Port}/sdk/command", content);
            Console.WriteLine($"[msi] SetZoneColor zone={zone}({zoneIdx}) {hex} → {resp.StatusCode}");

            if (resp.IsSuccessStatusCode)
            {
                _zoneColors[zone] = (r, g, b);
                StartKeepAlive();
                return true;
            }
            // If that failed, log the response body so we can debug the format
            string body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[msi] SetZoneColor response body: {body}");
            return false;
        }
        catch (Exception ex) { Console.WriteLine($"[msi] SetZoneColor error: {ex.Message}"); return false; }
    }

    // Keep-alive: MSI Center SDK may revert colors if not refreshed periodically.
    private void StartKeepAlive()
    {
        _keepAlive ??= new System.Threading.Timer(async _ =>
        {
            foreach (var (zone, col) in _zoneColors)
                await SetZoneColorAsync(zone, col.r, col.g, col.b);
        }, null, 5000, 5000);
    }

    public void Dispose()
    {
        _keepAlive?.Dispose();
        _keepAlive = null;
    }
}
