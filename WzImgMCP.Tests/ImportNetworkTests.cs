#if LINUX_BUILD
using System.Net;
using System.Security.Cryptography;
using WzImgMCP.Import;

namespace WzImgMCP.Tests;

/// <summary>Which links and addresses an import accepts, and how the download behaves. No test touches the internet.</summary>
[Collection("AllowedRootEnv")]
public sealed class ImportNetworkTests
{
    private static readonly byte[] Png = PngMaker.Png();
    private const string Direct = ImportEnv.DirectLink;

    // ---- the address policy ----

    [Theory]
    [InlineData("http://drive.google.com/uc?id=abc", "url_not_allowed")]                 // not https
    [InlineData("ftp://drive.google.com/x", "url_not_allowed")]
    [InlineData("file:///etc/passwd", "url_not_allowed")]
    [InlineData("https://evil.example.com/a.png", "url_not_allowed")]                    // not on the list
    [InlineData("https://drive.google.com.evil.com/a.png", "url_not_allowed")]           // look-alike
    [InlineData("https://evildrive.google.com/a.png", "url_not_allowed")]
    [InlineData("https://googleusercontent.com/a.png", "url_not_allowed")]               // the wildcard needs a sub domain
    [InlineData("https://10.0.0.1/a.png", "url_not_allowed")]                            // IP literals
    [InlineData("https://192.168.2.160/a.png", "url_not_allowed")]
    [InlineData("https://[::1]/a.png", "url_not_allowed")]
    [InlineData("https://127.0.0.1/a.png", "url_not_allowed")]
    [InlineData("https://user:pw@drive.google.com/a.png", "url_not_allowed")]            // credentials in the link
    [InlineData("https://drive.google.com:8443/a.png", "url_not_allowed")]               // other port
    [InlineData("not a url", "bad_url")]
    [InlineData("", "bad_url")]
    [InlineData("relative/path", "bad_url")]
    [InlineData("/relative/path", "url_not_allowed")]   // on Linux this reads as a file address
    public void Fetch_RefusesLinksThatAreNotSecureAllowedDriveLinks(string url, string code)
    {
        using var env = new ImportEnv();
        ImportEnv.AssertRefused(env.Service.Fetch(url, "files", null), code);
        Assert.Empty(env.Web.Requests);
        Assert.False(Directory.Exists(env.Inbox));   // nothing was staged
    }

    [Fact]
    public void Fetch_AcceptsTheGoogleHostsAndTheirSubdomains()
    {
        using var env = new ImportEnv();
        foreach (var url in new[] { "https://drive.google.com/uc?id=abcdefghij1", "https://docs.google.com/uc?id=abcdefghij1", "https://doc-0s-ab-docs.googleusercontent.com/docs/x" })
        {
            var result = env.Service.Fetch(url, "files", null);
            Assert.True(result.Success, result.Error);
            var id = ((ImportFetchResult)result).Id!;
            env.Service.WaitForJobAsync(id, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            env.Service.Undo(id);   // free the place
        }
    }

    [Fact]
    public void AllowedHosts_AreConfigurableByEnvironment()
    {
        var options = ImportOptions.FromEnvironment(name => name == ImportOptions.AllowedHostsVar ? "files.example.org, *.cdn.example.org" : null);
        Assert.True(options.IsHostAllowed("files.example.org"));
        Assert.True(options.IsHostAllowed("a.cdn.example.org"));
        Assert.False(options.IsHostAllowed("cdn.example.org"));
        Assert.False(options.IsHostAllowed("drive.google.com"));
        Assert.False(options.IsHostAllowed("10.0.0.1"));
        // a missing or empty value keeps the Drive defaults
        Assert.True(ImportOptions.FromEnvironment(_ => null).IsHostAllowed("drive.google.com"));
        Assert.True(ImportOptions.FromEnvironment(_ => "  ").IsHostAllowed("drive.usercontent.google.com"));
    }

    [Fact]
    public void Options_ReadLimitsFromEnvironment_AndIgnoreBadValues()
    {
        var env = new Dictionary<string, string>
        {
            [ImportOptions.MaxMbVar] = "50", [ImportOptions.MaxStagedVar] = "2", [ImportOptions.RetentionDaysVar] = "3",
            [ImportOptions.FilesMaxCountVar] = "abc", [ImportOptions.FilesMaxRatioVar] = "-4",
        };
        var o = ImportOptions.FromEnvironment(n => env.GetValueOrDefault(n));
        Assert.Equal(50L * 1024 * 1024, o.MaxDownloadBytes);
        Assert.Equal(2, o.MaxStaged);
        Assert.Equal(TimeSpan.FromDays(3), o.Retention);
        Assert.Equal(200, o.FilesMaxCount);   // bad value: default
        Assert.Equal(50, o.FilesMaxRatio);
        var defaults = ImportOptions.FromEnvironment(_ => null);
        Assert.Equal(300L * 1024 * 1024, defaults.MaxDownloadBytes);
        Assert.Equal(3, defaults.MaxStaged);
        Assert.Equal(TimeSpan.FromDays(7), defaults.Retention);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.9.9.9", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.2.160", true)]          // the home server
    [InlineData("192.168.2.118", true)]
    [InlineData("169.254.169.254", true)]        // link-local, cloud metadata
    [InlineData("100.64.0.1", true)]             // shared address space
    [InlineData("0.0.0.0", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("::1", true)]
    [InlineData("::", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("ff02::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]        // IPv4 inside IPv6
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("::ffff:192.168.1.1", true)]
    [InlineData("64:ff9b::7f00:1", true)]        // NAT64 form of 127.0.0.1
    [InlineData("2002:7f00:1::1", true)]         // 6to4 form of 127.0.0.1
    [InlineData("2001:db8::1", true)]
    [InlineData("142.250.74.110", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2a00:1450:4001:81b::200e", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void IsForbiddenAddress_BlocksEverythingThatIsNotPublic(string text, bool forbidden)
        => Assert.Equal(forbidden, ImportUrlPolicy.IsForbiddenAddress(IPAddress.Parse(text)));

    [Fact]
    public async Task ResolvePublic_RefusesAMixedAnswerAndEmptyAnswers()
    {
        HostResolver mixed = (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("192.168.1.5") });
        var ex = await Assert.ThrowsAsync<ImportException>(() => ImportUrlPolicy.ResolvePublicAsync("drive.google.com", mixed, default));
        Assert.Equal("address_not_allowed", ex.Code);
        HostResolver none = (_, _) => Task.FromResult(Array.Empty<IPAddress>());
        Assert.Equal("dns_failed", (await Assert.ThrowsAsync<ImportException>(() => ImportUrlPolicy.ResolvePublicAsync("drive.google.com", none, default))).Code);
        HostResolver broken = (_, _) => throw new InvalidOperationException("no dns");
        Assert.Equal("dns_failed", (await Assert.ThrowsAsync<ImportException>(() => ImportUrlPolicy.ResolvePublicAsync("drive.google.com", broken, default))).Code);
    }

    [Fact]
    public async Task GuardedHandler_RefusesToConnectToAPrivateAddress()
    {
        // the real handler resolves the name itself at connect time; a name that points inside is refused before any socket opens
        HostResolver inside = (_, _) => Task.FromResult(new[] { IPAddress.Parse("127.0.0.1") });
        using var client = new HttpClient(ImportDownloader.CreateGuardedHandler(inside));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://drive.google.com/x"));
        var inner = Assert.IsType<ImportException>(ex.InnerException);
        Assert.Equal("address_not_allowed", inner.Code);
    }

    [Theory]
    [InlineData("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUv/view?usp=sharing", "https://drive.usercontent.google.com/download?id=1AbCdEfGhIjKlMnOpQrStUv&export=download&confirm=t")]
    [InlineData("https://drive.google.com/open?id=1AbCdEfGhIjKlMnOpQrStUv", "https://drive.usercontent.google.com/download?id=1AbCdEfGhIjKlMnOpQrStUv&export=download&confirm=t")]
    [InlineData("https://drive.google.com/uc?export=download&id=1AbCdEfGhIjKlMnOpQrStUv", "https://drive.usercontent.google.com/download?id=1AbCdEfGhIjKlMnOpQrStUv&export=download&confirm=t")]
    [InlineData("https://docs.google.com/uc?id=1AbCdEfGhIjKlMnOpQrStUv", "https://drive.usercontent.google.com/download?id=1AbCdEfGhIjKlMnOpQrStUv&export=download&confirm=t")]
    [InlineData("https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUv", "https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUv")]
    [InlineData("https://drive.google.com/file/d/short/view", "https://drive.google.com/file/d/short/view")]
    public void DriveLinks_ShareLinksBecomeDirectDownloads(string input, string expected)
        => Assert.Equal(expected, DriveLinks.Normalize(new Uri(input)).ToString());

    [Fact]
    public void DriveLinks_ReadsTheVirusScanForm()
    {
        const string html = """
            <html><body><p>Google Drive can't scan this file for viruses.</p>
            <form id="download-form" action="https://drive.usercontent.google.com/download" method="get">
              <input type="submit" id="uc-download-link" value="Download anyway">
              <input type="hidden" name="id" value="1AbC">
              <input type="hidden" name="export" value="download">
              <input type="hidden" name="confirm" value="t">
              <input type="hidden" name="uuid" value="u-u-i-d">
            </form></body></html>
            """;
        var target = DriveLinks.ReadVirusScanForm(html, new Uri(Direct));
        Assert.NotNull(target);
        Assert.Equal("drive.usercontent.google.com", target!.Host);
        Assert.Equal("?id=1AbC&export=download&confirm=t&uuid=u-u-i-d", target.Query);
        Assert.Null(DriveLinks.ReadVirusScanForm("<html>Sign in to continue</html>", new Uri(Direct)));
        var old = DriveLinks.ReadVirusScanForm("<a href=\"/uc?export=download&amp;confirm=AbCd&amp;id=1AbC\">Download anyway</a>", new Uri("https://drive.google.com/uc?id=1AbC"));
        Assert.Equal("https://drive.google.com/uc?export=download&confirm=AbCd&id=1AbC", old!.ToString());
    }

    // ---- downloads ----

    [Fact]
    public async Task Fetch_DownloadsFromAShareLink_AndStagesItUnderTheInbox()
    {
        using var env = new ImportEnv();
        env.Web.ServeFile(Direct, Png, "sprite.png");

        var status = await env.FetchAndWait(ImportEnv.ViewLink, "files");

        Assert.Equal(ImportStatus.Ready, status.Status);
        Assert.Equal(ImportEnv.DirectLink, env.Web.Requests.Single().ToString());   // the share link was turned into the direct download
        var file = Assert.Single(status.Files!);
        Assert.Equal("sprite.png", file.Name);
        Assert.Equal(Path.Combine(env.Inbox, status.Id!, "files", "sprite.png"), file.Path);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant(), status.Sha256);
        Assert.Equal(Png.Length, status.BytesDownloaded);
        Assert.True(File.Exists(file.Path));
        Assert.StartsWith(env.Inbox + Path.DirectorySeparatorChar, file.Path);
    }

    [Fact]
    public async Task Fetch_FollowsGoogleVirusScanPage()
    {
        using var env = new ImportEnv();
        env.Web.Route(Direct, () => FakeWeb.Html("""
            <form id="download-form" action="https://drive.usercontent.google.com/download" method="get">
            <input type="hidden" name="id" value="X"><input type="hidden" name="confirm" value="t"><input type="hidden" name="uuid" value="123">
            </form>
            """));
        env.Web.ServeFile("https://drive.usercontent.google.com/download?id=X&confirm=t&uuid=123", Png, "a.png");

        var status = await env.FetchAndWait(Direct, "files");

        Assert.Equal(ImportStatus.Ready, status.Status);
        Assert.Equal(2, env.Web.Requests.Count);
        Assert.Contains("uuid=123", env.Web.Requests.Last().Query);
    }

    [Fact]
    public async Task Fetch_VirusScanPageThatLeadsOutsideTheAllowList_IsRefused()
    {
        using var env = new ImportEnv();
        env.Web.Route(Direct, () => FakeWeb.Html("<form id=\"download-form\" action=\"https://evil.example.com/steal\"><input type=\"hidden\" name=\"id\" value=\"X\"></form>"));
        var status = await env.FetchAndWait(Direct, "files");
        Assert.Equal(ImportStatus.Failed, status.Status);
        Assert.Equal("url_not_allowed", status.JobErrorCode);
        Assert.Single(env.Web.Requests);
    }

    [Fact]
    public async Task Fetch_FollowsRedirectsThatStayOnTheAllowList()
    {
        using var env = new ImportEnv();
        env.Web.Redirect(Direct, "https://doc-0s-ab-docs.googleusercontent.com/docs/securesc/x?e=download");
        env.Web.ServeFile("https://doc-0s-ab-docs.googleusercontent.com/docs/securesc/x?e=download", Png, "a.png");
        var status = await env.FetchAndWait(Direct, "files");
        Assert.Equal(ImportStatus.Ready, status.Status);
    }

    [Theory]
    [InlineData("https://evil.example.com/file.png", "redirect_not_allowed")]       // another host
    [InlineData("http://drive.google.com/file.png", "redirect_not_allowed")]        // https downgrade
    [InlineData("https://127.0.0.1/file.png", "redirect_not_allowed")]              // an address
    [InlineData("https://192.168.2.160:9000/file.png", "redirect_not_allowed")]     // the home server
    [InlineData("https://user:pw@drive.google.com/file.png", "redirect_not_allowed")]
    public async Task Fetch_RedirectToAForbiddenTarget_IsRefused_AndNeverRequested(string target, string code)
    {
        using var env = new ImportEnv();
        env.Web.Redirect(Direct, target);
        env.Web.ServeFile(target, Png, "x.png");
        var status = await env.FetchAndWait(Direct, "files");
        Assert.Equal(ImportStatus.Failed, status.Status);
        Assert.Equal(code, status.JobErrorCode);
        Assert.Single(env.Web.Requests);                                              // only the first hop was ever requested
        Assert.False(Directory.Exists(Path.Combine(env.Inbox, status.Id!, "files")));
    }

    [Fact]
    public async Task Fetch_RedirectLoop_StopsAfterTheLimit()
    {
        using var env = new ImportEnv();
        env.Web.Redirect(Direct, Direct + "&x=1");
        env.Web.Redirect(Direct + "&x=1", Direct);
        var status = await env.FetchAndWait(Direct, "files");
        Assert.Equal("too_many_redirects", status.JobErrorCode);
        Assert.Equal(env.Options.MaxRedirects + 1, env.Web.Requests.Count);
    }

    [Fact]
    public async Task Fetch_NameThatResolvesToTheHomeNetwork_IsRefused_BeforeAnyRequest()
    {
        using var env = new ImportEnv();
        env.Web.Dns["drive.usercontent.google.com"] = new[] { IPAddress.Parse("192.168.2.160") };
        var status = await env.FetchAndWait(Direct, "files");
        Assert.Equal(ImportStatus.Failed, status.Status);
        Assert.Equal("address_not_allowed", status.JobErrorCode);
        Assert.Empty(env.Web.Requests);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.7")]
    [InlineData("::1")]
    [InlineData("fd12:3456::1")]
    public async Task Fetch_NameThatResolvesToAPrivateAddress_IsRefused(string address)
    {
        using var env = new ImportEnv();
        env.Web.Dns["drive.usercontent.google.com"] = new[] { IPAddress.Parse(address) };
        var status = await env.FetchAndWait(Direct, "files");
        Assert.Equal("address_not_allowed", status.JobErrorCode);
        Assert.Empty(env.Web.Requests);
    }

    [Fact]
    public async Task Fetch_RedirectTargetThatResolvesToAPrivateAddress_IsRefused_BeforeRequestingIt()
    {
        using var env = new ImportEnv();
        env.Web.Redirect(Direct, "https://doc-0s-ab-docs.googleusercontent.com/x");
        env.Web.Dns["doc-0s-ab-docs.googleusercontent.com"] = new[] { IPAddress.Parse("10.1.1.1") };
        env.Web.ServeFile("https://doc-0s-ab-docs.googleusercontent.com/x", Png, "a.png");
        var status = await env.FetchAndWait(Direct, "files");
        Assert.Equal("address_not_allowed", status.JobErrorCode);
        Assert.Single(env.Web.Requests);
    }

    [Fact]
    public async Task Fetch_OverTheSizeCap_IsRefused_ByHeader_AndWhileStreaming()
    {
        using var env = new ImportEnv(o => new ImportOptions { MaxDownloadBytes = 1000 });
        env.Web.ServeFile(Direct, new byte[5000], "big.png");
        var byHeader = await env.FetchAndWait(Direct, "files");
        Assert.Equal("too_large", byHeader.JobErrorCode);
        Assert.Equal(0, byHeader.BytesDownloaded);   // refused before reading the body

        env.Web.Route(Direct + "&n=1", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new NoLengthContent(new byte[5000]) });
        var streaming = await env.FetchAndWait(Direct + "&n=1", "files");
        Assert.Equal("too_large", streaming.JobErrorCode);
        Assert.Empty(Directory.EnumerateFiles(env.DirOf(streaming.Id!), "*", SearchOption.AllDirectories).Where(f => f.EndsWith("download.part")));   // the partial file is gone
    }

    [Fact]
    public async Task Fetch_WrongHash_IsRefused_AndRightHashIsAccepted_InAnyCase()
    {
        using var env = new ImportEnv();
        env.Web.ServeFile(Direct, Png, "a.png");
        var wrong = await env.FetchAndWait(Direct, "files", new string('0', 64));
        Assert.Equal("hash_mismatch", wrong.JobErrorCode);
        Assert.Equal(ImportStatus.Failed, wrong.Status);
        Assert.False(Directory.Exists(Path.Combine(env.DirOf(wrong.Id!), "files")));

        var right = Convert.ToHexString(SHA256.HashData(Png));   // upper case
        var ok = await env.FetchAndWait(Direct, "files", right);
        Assert.Equal(ImportStatus.Ready, ok.Status);

        ImportEnv.AssertRefused(env.Service.Fetch(Direct, "files", "not-a-hash"), "bad_hash");
        ImportEnv.AssertRefused(env.Service.Fetch(Direct, "files", new string('g', 64)), "bad_hash");
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task Fetch_ServerErrors_AreReportedPlainly(int code)
    {
        using var env = new ImportEnv();
        env.Web.Route(Direct, () => new HttpResponseMessage((HttpStatusCode)code));
        var status = await env.FetchAndWait(Direct, "files");
        Assert.Equal("http_error", status.JobErrorCode);
        Assert.Contains(code.ToString(), status.JobError);
    }

    [Fact]
    public async Task Fetch_WebPageWithoutADownload_IsNotAFile()
    {
        using var env = new ImportEnv();
        env.Web.Route(Direct, () => FakeWeb.Html("<html>Sign in</html>"));
        var status = await env.FetchAndWait(Direct, "files");
        Assert.Equal("not_a_file", status.JobErrorCode);
    }

    [Fact]
    public async Task Fetch_StalledDownload_IsStopped()
    {
        using var env = new ImportEnv(o => new ImportOptions { StallTimeout = TimeSpan.FromMilliseconds(400) });
        env.Web.Route(Direct, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallStream()) });
        var status = await env.FetchAndWait(Direct, "files", seconds: 30);
        Assert.Equal("timeout", status.JobErrorCode);
    }

    [Fact]
    public async Task Fetch_TakesLongerThanTheTimeLimit_IsStopped()
    {
        using var env = new ImportEnv(o => new ImportOptions { DownloadTimeout = TimeSpan.FromMilliseconds(400) });
        env.Web.Route(Direct, async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(HttpStatusCode.OK); });
        var status = await env.FetchAndWait(Direct, "files", seconds: 30);
        Assert.Equal("timeout", status.JobErrorCode);
    }

    [Fact]
    public void Fetch_NeedsAnAllowedRoot()
    {
        using var env = new ImportEnv(setRoot: false);
        ImportEnv.AssertRefused(env.Service.Fetch(Direct, "files", null), "import_disabled");
        ImportEnv.AssertRefused(env.Service.GetStatus(new string('a', 32)), "import_disabled");
    }

    [Fact]
    public void Fetch_BadKind_IsRefused()
    {
        using var env = new ImportEnv();
        ImportEnv.AssertRefused(env.Service.Fetch(Direct, "mp3", null), "bad_kind");
        ImportEnv.AssertRefused(env.Service.Fetch(Direct, "files", null, "Skill"), "bad_kind");
    }

    // ---- limits on concurrent work ----

    [Fact]
    public async Task OnlyOneDownloadAtATime_AndAtMostThreeStaged()
    {
        using var env = new ImportEnv();
        var gate = new TaskCompletionSource();
        env.Web.Route(Direct + "&slow=1", async (_, ct) => { await gate.Task.WaitAsync(ct); return FakeWeb.File(Png, "slow.png"); });
        env.Web.ServeFile(Direct, Png, "a.png");

        var first = Assert.IsType<ImportFetchResult>(env.Service.Fetch(Direct + "&slow=1", "files", null));
        var busy = env.Service.Fetch(Direct, "files", null);
        ImportEnv.AssertRefused(busy, "busy");
        Assert.Contains(first.Id!, busy.Error);

        gate.SetResult();
        await env.Service.WaitForJobAsync(first.Id!, TimeSpan.FromSeconds(30));
        Assert.Equal(ImportStatus.Ready, ((ImportStatusResult)env.Service.GetStatus(first.Id)).Status);

        var second = await env.FetchAndWait(Direct, "files");
        var third = await env.FetchAndWait(Direct, "files");
        Assert.Equal(ImportStatus.Ready, third.Status);

        // three are staged: a fourth is refused until a place is freed
        ImportEnv.AssertRefused(env.Service.Fetch(Direct, "files", null), "too_many_staged");
        Assert.True(env.Service.Undo(second.Id).Success);
        var fourth = await env.FetchAndWait(Direct, "files");
        Assert.Equal(ImportStatus.Ready, fourth.Status);
    }

    [Fact]
    public async Task FailedImports_DoNotTakeAPlace()
    {
        using var env = new ImportEnv(o => new ImportOptions { MaxStaged = 1 });
        env.Web.Route(Direct, () => new HttpResponseMessage(HttpStatusCode.NotFound));
        var failed = await env.FetchAndWait(Direct, "files");
        Assert.Equal(ImportStatus.Failed, failed.Status);
        env.Web.ServeFile(Direct, Png, "a.png");
        var ok = await env.FetchAndWait(Direct, "files");
        Assert.Equal(ImportStatus.Ready, ok.Status);
        // the failed report was tidied away when the next import started
        ImportEnv.AssertRefused(env.Service.GetStatus(failed.Id), "not_found");
    }

    [Fact]
    public async Task Undo_CancelsADownloadThatIsStillRunning()
    {
        using var env = new ImportEnv();
        env.Web.Route(Direct, async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return FakeWeb.File(Png, "x.png"); });
        var fetch = Assert.IsType<ImportFetchResult>(env.Service.Fetch(Direct, "files", null));
        await WaitUntil(() => env.Web.Requests.Count == 1);

        var undo = Assert.IsType<ImportUndoResult>(env.Service.Undo(fetch.Id));
        Assert.True(undo.Success);
        Assert.Equal("cancelled", undo.Outcome);
        await env.Service.WaitForJobAsync(fetch.Id!, TimeSpan.FromSeconds(30));
        ImportEnv.AssertRefused(env.Service.GetStatus(fetch.Id), "not_found");
        Assert.False(Directory.Exists(env.DirOf(fetch.Id!)));
        // and a new import may start
        env.Web.ServeFile(Direct + "&b=1", Png, "b.png");
        Assert.Equal(ImportStatus.Ready, (await env.FetchAndWait(Direct + "&b=1", "files")).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../etc")]
    [InlineData("..")]
    [InlineData("0123456789abcdef0123456789ABCDEF")]   // upper case
    [InlineData("0123456789abcdef0123456789abcde")]    // too short
    [InlineData("0123456789abcdef0123456789abcdef0")]  // too long
    [InlineData("0123456789abcdef/../../../x1234")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void EveryToolRefusesAnIdThatIsNotOneTheServerMade(string id)
    {
        using var env = new ImportEnv();
        ImportEnv.AssertRefused(env.Service.GetStatus(id), "bad_id");
        ImportEnv.AssertRefused(env.Service.Preview(id), "bad_id");
        ImportEnv.AssertRefused(env.Service.Apply(id, "t"), "bad_id");
        ImportEnv.AssertRefused(env.Service.Undo(id), "bad_id");
    }

    [Fact]
    public void AnUnknownButWellFormedId_IsNotFound()
    {
        using var env = new ImportEnv();
        ImportEnv.AssertRefused(env.Service.GetStatus(new string('a', 32)), "not_found");
    }

    private static async Task WaitUntil(Func<bool> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "condition not reached in time");
            await Task.Delay(20);
        }
    }

    private sealed class NoLengthContent : HttpContent
    {
        private readonly byte[] _data;
        public NoLengthContent(byte[] data) { _data = data; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_data, 0, _data.Length);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    /// <summary>Gives three bytes and then waits for ever (until it is cancelled).</summary>
    private sealed class StallStream : Stream
    {
        private bool _first = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_first) { _first = false; buffer.Span[0] = 1; return 1; }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
#endif
