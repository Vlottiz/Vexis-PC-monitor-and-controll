using System.Runtime.InteropServices;

/// <summary>
/// Dark title bars. Windows draws a white title bar on every window unless the app asks for
/// a dark one (Windows 10 2004+ and 11). On Windows 11 the bar can also take an exact colour,
/// so it is painted in the theme's background colour with the title in the theme's header
/// colour, and the window border matches. Older Windows ignores what it doesn't support.
/// </summary>
public static class WindowChrome
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DarkModeOld = 19, DarkMode = 20;          // DWMWA_USE_IMMERSIVE_DARK_MODE (before / from Win10 2004)
    private const int BorderColor = 34, CaptionColor = 35, TextColor = 36;   // Windows 11 only

    /// <summary>Applies the theme to a window's title bar. Colours come from the saved theme;
    /// with none saved yet, the default theme's.</summary>
    public static void Apply(Form form, IDictionary<string, string>? colors)
    {
        if (!OperatingSystem.IsWindows() || !form.IsHandleCreated) return;
        try
        {
            IntPtr h = form.Handle;
            int on = 1;
            if (DwmSetWindowAttribute(h, DarkMode, ref on, 4) != 0) DwmSetWindowAttribute(h, DarkModeOld, ref on, 4);
            int bg   = ColorRef(colors, "bg", 0x0a, 0x08, 0x08);
            int text = ColorRef(colors, "headerTitle", 0xff, 0xcc, 0x00);
            DwmSetWindowAttribute(h, CaptionColor, ref bg, 4);
            DwmSetWindowAttribute(h, BorderColor, ref bg, 4);
            DwmSetWindowAttribute(h, TextColor, ref text, 4);
        }
        catch (Exception ex) { Console.WriteLine($"[ui] Title bar colour not applied: {ex.Message}"); }
    }

    // Windows wants 0x00BBGGRR
    private static int ColorRef(IDictionary<string, string>? colors, string key, int r, int g, int b)
    {
        if (colors != null && colors.TryGetValue(key, out var hex) && hex is { Length: 7 } && hex[0] == '#' &&
            int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int v))
        { r = (v >> 16) & 0xff; g = (v >> 8) & 0xff; b = v & 0xff; }
        return r | (g << 8) | (b << 16);
    }
}
