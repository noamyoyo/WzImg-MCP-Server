namespace WzImgMCP.Utils;

/// <summary>Formats a bind address for use in a URL.</summary>
public static class HostUrl
{
    /// <summary>
    /// Puts an IPv6 literal (anything containing a colon) in square brackets, as required in
    /// <c>http://[::]:13339</c>. Host names, IPv4 addresses and already bracketed values are unchanged.
    /// </summary>
    public static string ForUrl(string host)
    {
        host = host.Trim();
        if (host.Contains(':') && !host.StartsWith('['))
            return "[" + host + "]";
        return host;
    }
}
