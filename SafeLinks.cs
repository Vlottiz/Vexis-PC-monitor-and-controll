/// <summary>
/// Checks links before they are opened with the shell. Process.Start with
/// UseShellExecute runs whatever it is given — a web page, but also a program, a
/// script or a file — so only real https:// web addresses are let through.
/// </summary>
public static class SafeLinks
{
    /// <summary>True for an absolute https:// URL with a host; <paramref name="link"/> is the normalised form to open.</summary>
    public static bool IsWebLink(string? url, out string link)
    {
        link = "";
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host)) return false;
        link = uri.AbsoluteUri;
        return true;
    }
}
