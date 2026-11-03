using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WzImgMCP.Import;

/// <summary>A refusal with a stable code and a plain sentence for the caller. The message never holds a path, a URL query or a secret.</summary>
public sealed class ImportException : Exception
{
    public string Code { get; }
    public ImportException(string code, string message) : base(message) { Code = code; }
}

/// <summary>Resolves a host name to its addresses. A parameter so tests need no network.</summary>
public delegate Task<IPAddress[]> HostResolver(string host, CancellationToken cancellationToken);

/// <summary>
/// Which web addresses an import may fetch, and which network addresses a connection may reach. Everything here
/// refuses by default: https only, a host on the allow-list, no IP literals, no user name in the URL, port 443, and after
/// DNS resolution no loopback, private, link-local, shared, multicast or otherwise non-public address.
/// </summary>
public static class ImportUrlPolicy
{
    public const int MaxUrlLength = 2048;

    /// <summary>Parses and checks the address as the caller typed it (before any redirect).</summary>
    public static Uri ParseAndCheck(string? url, ImportOptions options)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ImportException("bad_url", "The link is empty.");
        url = url.Trim();
        if (url.Length > MaxUrlLength)
            throw new ImportException("bad_url", $"The link is longer than {MaxUrlLength} characters.");
        if (url.Any(char.IsControl) || url.Any(char.IsWhiteSpace))
            throw new ImportException("bad_url", "The link contains spaces or control characters.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ImportException("bad_url", "The link is not a valid web address.");
        Check(uri, options, redirect: false);
        return uri;
    }

    /// <summary>
    /// Checks one address. The first link must be on the allowed hosts; a redirect target or the target of Google's virus-scan
    /// form (<paramref name="redirect"/>) may also be on the redirect hosts. Does not resolve the name.
    /// </summary>
    public static void Check(Uri uri, ImportOptions options, bool redirect)
    {
        if (!uri.IsAbsoluteUri)
            throw new ImportException("bad_url", "The link is not a valid web address.");
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ImportException("url_not_allowed", "Only secure links (https) are accepted.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ImportException("url_not_allowed", "Links with a user name or password in them are not accepted.");
        if (uri.Port != 443)
            throw new ImportException("url_not_allowed", "Only the standard https port (443) is accepted.");
        var host = uri.IdnHost;
        if (IPAddress.TryParse(host.Trim('[', ']'), out _))
            throw new ImportException("url_not_allowed", "Links to an IP address are not accepted; use the normal link of the file.");
        if (!(redirect ? options.IsRedirectHostAllowed(host) : options.IsHostAllowed(host)))
            throw new ImportException("url_not_allowed", $"The host {host} is not on the list of allowed sites ({string.Join(", ", options.AllowedHosts)}).");
    }

    /// <summary>
    /// Resolves the host and refuses when any of its addresses is not public. A mixed answer is refused as a whole:
    /// a host that has one private address among public ones is not trusted.
    /// </summary>
    public static async Task<IPAddress[]> ResolvePublicAsync(string host, HostResolver resolver, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await resolver(host, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ImportException("dns_failed", $"The host {host} could not be found.");
        }
        if (addresses.Length == 0)
            throw new ImportException("dns_failed", $"The host {host} could not be found.");
        RequireAllPublic(host, addresses);
        return addresses;
    }

    public static void RequireAllPublic(string host, IEnumerable<IPAddress> addresses)
    {
        foreach (var address in addresses)
        {
            if (IsForbiddenAddress(address))
                throw new ImportException("address_not_allowed", $"The host {host} points to an address that is not on the public internet; the download was refused.");
        }
    }

    /// <summary>True for every address that is not an ordinary public unicast address.</summary>
    public static bool IsForbiddenAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            return true;   // a scoped (link-local style) address
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            int a = b[0], c = b[1];
            if (a == 0) return true;                                   // this network
            if (a == 10) return true;                                  // private
            if (a == 100 && c >= 64 && c <= 127) return true;          // shared address space (carrier grade NAT)
            if (a == 127) return true;                                 // loopback
            if (a == 169 && c == 254) return true;                     // link-local
            if (a == 172 && c >= 16 && c <= 31) return true;           // private
            if (a == 192 && c == 0 && b[2] == 0) return true;          // IETF protocol assignments
            if (a == 192 && c == 0 && b[2] == 2) return true;          // documentation
            if (a == 192 && c == 88 && b[2] == 99) return true;        // 6to4 relay
            if (a == 192 && c == 168) return true;                     // private (the home network)
            if (a == 198 && (c == 18 || c == 19)) return true;         // benchmarking
            if (a == 198 && c == 51 && b[2] == 100) return true;       // documentation
            if (a == 203 && c == 0 && b[2] == 113) return true;        // documentation
            if (a >= 224) return true;                                 // multicast, reserved, broadcast
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Only global unicast 2000::/3 can be public. That already leaves out ::, ::1, fc00::/7, fe80::/10, ff00::/8,
            // ::ffff:0:0/96 (handled above), 64:ff9b::/96 and the compatible-IPv4 range.
            if ((b[0] & 0xE0) != 0x20) return true;
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return true;   // 2001::/32 Teredo
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return true;   // 2001:db8::/32 documentation
            if (b[0] == 0x20 && b[1] == 0x02) return true;                                    // 2002::/16 6to4 (embeds an IPv4 address)
            return false;
        }

        return true;
    }
}

/// <summary>Turns the share links people paste into the direct download address, and reads Google's "can't scan for viruses" page.</summary>
public static class DriveLinks
{
    private static readonly Regex FileId = new("^[A-Za-z0-9_-]{10,128}$", RegexOptions.Compiled);

    /// <summary>
    /// drive.google.com/file/d/ID/view, /open?id=ID and /uc?id=ID become
    /// https://drive.usercontent.google.com/download?id=ID&amp;export=download&amp;confirm=t (confirm=t answers the
    /// virus-scan question for big files up front). Any other address is returned unchanged.
    /// </summary>
    public static Uri Normalize(Uri uri)
    {
        var host = uri.IdnHost.ToLowerInvariant();
        if (host is not ("drive.google.com" or "docs.google.com"))
            return uri;

        string? id = null;
        var m = Regex.Match(uri.AbsolutePath, @"^/(?:[a-z]/[^/]+/)?file/d/([^/]+)", RegexOptions.IgnoreCase);
        if (m.Success)
            id = m.Groups[1].Value;
        else if (uri.AbsolutePath is "/open" or "/uc" or "/u/0/uc" || uri.AbsolutePath.EndsWith("/uc", StringComparison.Ordinal))
            id = QueryValue(uri.Query, "id");

        if (id == null || !FileId.IsMatch(id))
            return uri;
        return new Uri($"https://drive.usercontent.google.com/download?id={Uri.EscapeDataString(id)}&export=download&confirm=t");
    }

    private static string? QueryValue(string query, string key)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var k = eq < 0 ? part : part.Substring(0, eq);
            if (k == key)
                return eq < 0 ? "" : Uri.UnescapeDataString(part.Substring(eq + 1));
        }
        return null;
    }

    /// <summary>
    /// Google answers a big file (and sometimes any file) with a page "Google Drive can't scan this file for viruses" that
    /// holds a form (id="download-form", method get) with hidden fields id, export, confirm and uuid; sending the form is
    /// the download. The older page had a link with confirm=. Returns the address the form leads to, or null when the page
    /// is not that page. The caller checks the returned address like any other.
    /// </summary>
    public static Uri? ReadVirusScanForm(string html, Uri pageUri)
    {
        var form = Regex.Match(html, @"<form\b[^>]*\bid=""download-form""[^>]*>(?<body>.*?)</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (form.Success)
        {
            var tag = Regex.Match(form.Value, @"<form\b[^>]*>", RegexOptions.IgnoreCase).Value;
            var action = Regex.Match(tag, @"\baction=""(?<a>[^""]*)""", RegexOptions.IgnoreCase);
            if (action.Success)
            {
                var fields = new List<string>();
                foreach (Match input in Regex.Matches(form.Groups["body"].Value, @"<input\b[^>]*>", RegexOptions.IgnoreCase))
                {
                    var name = Regex.Match(input.Value, @"\bname=""(?<n>[^""]*)""", RegexOptions.IgnoreCase);
                    var value = Regex.Match(input.Value, @"\bvalue=""(?<v>[^""]*)""", RegexOptions.IgnoreCase);
                    if (name.Success)
                        fields.Add(Uri.EscapeDataString(WebUtility.HtmlDecode(name.Groups["n"].Value)) + "=" +
                                   Uri.EscapeDataString(WebUtility.HtmlDecode(value.Success ? value.Groups["v"].Value : "")));
                }
                if (Uri.TryCreate(pageUri, WebUtility.HtmlDecode(action.Groups["a"].Value), out var target))
                {
                    var builder = new UriBuilder(target) { Query = string.Join("&", fields) };
                    return builder.Uri;
                }
            }
        }

        var link = Regex.Match(html, @"href=""(?<h>[^""]*[?&;]confirm=[^""]+)""", RegexOptions.IgnoreCase);
        if (link.Success && Uri.TryCreate(pageUri, WebUtility.HtmlDecode(link.Groups["h"].Value), out var next))
            return next;
        return null;
    }
}

/// <summary>What a finished download produced.</summary>
public sealed record DownloadResult(long Bytes, string Sha256, string? SuggestedName, string? ContentType);

/// <summary>
/// Downloads one file from an allowed host into a file the caller names. Redirects are followed by hand (at most
/// <see cref="ImportOptions.MaxRedirects"/>), and every address, the first and each redirect, is checked against the
/// allow-list and resolved to public addresses before a request is sent. In the real handler the connection itself
/// re-resolves and checks again (<see cref="CreateGuardedHandler"/>), so a name that changes its answer between the check and
/// the connection cannot reach a private address.
/// </summary>
public sealed class ImportDownloader
{
    private readonly ImportOptions _options;
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly HostResolver _resolver;

    public ImportDownloader(ImportOptions options, Func<HttpMessageHandler> handlerFactory, HostResolver resolver)
    {
        _options = options;
        _handlerFactory = handlerFactory;
        _resolver = resolver;
    }

    public static readonly HostResolver SystemResolver = async (host, ct) => await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);

    /// <summary>A handler that never follows redirects, never uses a proxy, and only connects to public addresses on port 443.</summary>
    public static HttpMessageHandler CreateGuardedHandler(HostResolver resolver)
        => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                if (context.DnsEndPoint.Port != 443)
                    throw new HttpRequestException("Only port 443 is allowed.");
                var addresses = await ImportUrlPolicy.ResolvePublicAsync(host, resolver, ct).ConfigureAwait(false);
                Exception? last = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, 443), ct).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex)
                    {
                        socket.Dispose();
                        last = ex;
                    }
                }
                throw new HttpRequestException("Could not connect.", last);
            },
        };

    /// <param name="beforeBody">Called with the file name the server suggested, once the headers are in and before any body is read; may throw to stop the download.</param>
    /// <param name="progress">Called with bytes so far and the total when the server said it.</param>
    public async Task<DownloadResult> DownloadAsync(Uri start, string destinationPath, Action<string?>? beforeBody,
        Action<long, long?>? progress, CancellationToken cancellationToken)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(_options.DownloadTimeout);
        try
        {
            return await RunAsync(start, destinationPath, beforeBody, progress, overall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ImportException("timeout", "The download took too long and was stopped.");
        }
    }

    private async Task<DownloadResult> RunAsync(Uri start, string destination, Action<string?>? beforeBody, Action<long, long?>? progress, CancellationToken ct)
    {
        using var client = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WzImgMCP-import/1.0");

        var current = DriveLinks.Normalize(start);
        int redirects = 0;
        bool formFollowed = false;
        bool firstHop = true;

        while (true)
        {
            ImportUrlPolicy.Check(current, _options, redirect: !firstHop);
            firstHop = false;
            await ImportUrlPolicy.ResolvePublicAsync(current.IdnHost, _resolver, ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.InnerException is ImportException inner)
            {
                throw inner;   // refused by the guarded connection
            }
            catch (HttpRequestException)
            {
                throw new ImportException("http_error", $"The server {current.IdnHost} could not be reached.");
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    if (location == null)
                        throw new ImportException("http_error", "The server sent a redirect without a target.");
                    if (++redirects > _options.MaxRedirects)
                        throw new ImportException("too_many_redirects", $"The link redirected more than {_options.MaxRedirects} times.");
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    try
                    {
                        ImportUrlPolicy.Check(next, _options, redirect: true);
                    }
                    catch (ImportException ex)
                    {
                        throw new ImportException("redirect_not_allowed", "The link redirects somewhere that is not allowed. " + ex.Message);
                    }
                    current = next;
                    continue;
                }

                if (status != 200)
                    throw new ImportException("http_error", status is 401 or 403 or 404
                        ? $"The server answered {status}: the file is missing or is not shared with \"anyone with the link\"."
                        : $"The server answered {status}.");

                var contentType = response.Content.Headers.ContentType?.MediaType;
                var total = response.Content.Headers.ContentLength;

                if (contentType != null && contentType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    var html = await ReadSmallTextAsync(response, ct).ConfigureAwait(false);
                    var form = formFollowed ? null : DriveLinks.ReadVirusScanForm(html, current);
                    if (form == null)
                        throw new ImportException("not_a_file", "The link gave a web page, not a file. Check that the file is shared with \"anyone with the link\" and that the link points to a file, not a folder.");
                    formFollowed = true;
                    current = form;
                    continue;
                }

                if (total.HasValue && total.Value > _options.MaxDownloadBytes)
                    throw new ImportException("too_large", $"The file is {total.Value / (1024 * 1024)} MB; the limit is {_options.MaxDownloadBytes / (1024 * 1024)} MB.");

                var suggested = SuggestedName(response.Content.Headers.ContentDisposition);
                beforeBody?.Invoke(suggested);
                RequireFreeSpace(destination, total);

                var (bytes, sha) = await StreamToFileAsync(response, destination, total, progress, ct).ConfigureAwait(false);
                return new DownloadResult(bytes, sha, suggested, contentType);
            }
        }
    }

    private static string? SuggestedName(ContentDispositionHeaderValue? header)
    {
        var name = header?.FileNameStar ?? header?.FileName;
        if (string.IsNullOrWhiteSpace(name))
            return null;
        name = name.Trim().Trim('"');
        return name.Length == 0 || name.Length > 255 ? null : name;
    }

    private static async Task<string> ReadSmallTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int limit = 512 * 1024;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[limit + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
        }
        if (total > limit)
            throw new ImportException("not_a_file", "The link gave a web page, not a file.");
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private void RequireFreeSpace(string destination, long? total)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(destination))!;
            var free = new DriveInfo(Path.GetPathRoot(dir)!).AvailableFreeSpace;
            // the download, what it unpacks to, and room for backups of what an apply replaces
            var need = (total ?? 0) * 3 + 64L * 1024 * 1024;
            if (free < need)
                throw new ImportException("disk_full", "There is not enough free disk space for this download.");
        }
        catch (ImportException)
        {
            throw;
        }
        catch (Exception)
        {
            // free space unknown (for instance an unusual file system): the write itself will fail if the disk is full
        }
    }

    private async Task<(long Bytes, string Sha256)> StreamToFileAsync(HttpResponseMessage response, string destination, long? total,
        Action<long, long?>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        var buffer = new byte[256 * 1024];
        while (true)
        {
            int read;
            using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                stall.CancelAfter(_options.StallTimeout);
                try
                {
                    read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ImportException("timeout", "The download stopped receiving data and was cancelled.");
                }
            }
            if (read <= 0) break;
            written += read;
            if (written > _options.MaxDownloadBytes)
                throw new ImportException("too_large", $"The file is bigger than the limit of {_options.MaxDownloadBytes / (1024 * 1024)} MB.");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            progress?.Invoke(written, total);
        }
        await target.FlushAsync(ct).ConfigureAwait(false);
        if (total.HasValue && written != total.Value)
            throw new ImportException("http_error", "The download ended early; the file is incomplete.");
        return (written, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
}
