using Microsoft.Web.WebView2.Core;

/// <summary>
/// One place for the WebView2 settings, used by the main window and the pop-outs.
/// The pages can ask an elevated process to end tasks, recycle files and change fans,
/// so the browser around them is locked down:
///  - no browser security flags turned off (no --disable-web-security etc.)
///  - only https://vexis.local/ pages can load; other links open in the normal browser
///  - messages are only accepted from https://vexis.local/ (checked by the callers)
///  - no host objects, and DevTools only in Debug builds
/// </summary>
public static class WebViewSecurity
{
    public static CoreWebView2EnvironmentOptions EnvironmentOptions() => new();

    public static void Apply(CoreWebView2 core)
    {
        var st = core.Settings;
        st.IsStatusBarEnabled           = false;
        st.AreHostObjectsAllowed        = false;
        st.AreDefaultContextMenusEnabled = true;   // copy / paste; "Inspect" only appears with DevTools on
#if DEBUG
        st.AreDevToolsEnabled = true;
#else
        st.AreDevToolsEnabled = false;
#endif
        core.NavigationStarting      += (_, e) => { if (!SafeLinks.IsAppPage(e.Uri)) { e.Cancel = true; OpenOutside(e.Uri); } };
        core.FrameNavigationStarting += (_, e) => { if (!SafeLinks.IsAppPage(e.Uri) && e.Uri != "about:blank") e.Cancel = true; };
        core.NewWindowRequested      += (_, e) => { e.Handled = true; OpenOutside(e.Uri); };
    }

    // A web link clicked inside the app goes to the user's normal browser, never into the app window
    private static void OpenOutside(string? uri)
    {
        if (SafeLinks.IsWebLink(uri, out var link))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = link, UseShellExecute = true });
        else
            Console.WriteLine($"[webview] Blocked navigation to {uri}");
    }
}
