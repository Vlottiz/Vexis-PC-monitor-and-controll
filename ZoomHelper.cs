using Microsoft.Web.WebView2.WinForms;

/// <summary>
/// Page zoom for the on-screen − / + buttons. Uses WebView2's own zoom — the same
/// thing Ctrl+scroll and Ctrl +/- change — so buttons and keyboard/mouse agree.
/// </summary>
public static class ZoomHelper
{
    public const double Min = 0.5, Max = 2.5, Step = 0.1;

    /// <summary>delta: +1 zoom in, -1 zoom out, 0 reset to 100%.</summary>
    public static double Change(WebView2 view, int delta)
    {
        double z = delta == 0 ? 1.0 : view.ZoomFactor + delta * Step;
        z = Math.Round(Math.Clamp(z, Min, Max), 2);
        view.ZoomFactor = z;
        return z;
    }

    public static double Parse(Dictionary<string, string>? settings) =>
        settings != null && settings.TryGetValue("zoom", out var v) &&
        double.TryParse(v, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var z)
            ? Math.Clamp(z, Min, Max) : 1.0;

    public static string Script(double zoom) =>
        $"typeof navZoomState==='function'&&navZoomState({Math.Round(zoom * 100)})";
}
