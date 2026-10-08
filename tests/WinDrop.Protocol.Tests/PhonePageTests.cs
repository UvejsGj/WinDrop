using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WinDrop.Cli;
using WinDrop.Protocol.Web;

namespace WinDrop.Protocol.Tests;

/// <summary>
/// The phone page over a real socket, driven by HttpClient where it can be and by hand
/// where a test needs to send what no browser would.
/// </summary>
public class PhonePageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"windrop-phone-{Guid.NewGuid():N}");
    private readonly CancellationTokenSource _stop = new();
    private readonly List<HttpClient> _clients = [];

    public void Dispose()
    {
        _stop.Cancel();
        foreach (HttpClient client in _clients) client.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private sealed record Running(PhonePageServer Server, string Token, int Port, HttpClient Client);

    private Running Start(
        Func<PhoneUploadRequest, CancellationToken, Task<bool>> consent,
        long maxUploadBytes = 8L * 1024 * 1024 * 1024,
        TimeSpan? idle = null)
    {
        string token = PhonePageServer.NewToken();

        var server = new PhonePageServer(new PhonePageOptions
        {
            DownloadDirectory = _dir,
            Token = token,
            ComputerName = "TEST-PC",
            ConsentHandler = consent,
            MaxUploadBytes = maxUploadBytes,
            IdleTimeout = idle ?? TimeSpan.FromSeconds(30),
        });

        int port = server.Listen(0);
        _ = server.RunAsync(_stop.Token);

        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/{token}/") };
        _clients.Add(client);
        return new Running(server, token, port, client);
    }

    private static Func<PhoneUploadRequest, CancellationToken, Task<bool>> Answer(bool accept, List<PhoneUploadRequest>? asked = null) =>
        (request, _) =>
        {
            if (asked is not null)
                lock (asked) asked.Add(request);

            return Task.FromResult(accept);
        };

    private static MultipartFormDataContent Form(string? manifest, params (string FileName, string Content)[] files)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent("iPhone"), "from");
        if (manifest is not null) form.Add(new StringContent(manifest), "manifest");

        foreach ((string fileName, string content) in files)
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", fileName);

        return form;
    }

    private static string Manifest(params (string Name, long Size)[] files) =>
        JsonSerializer.Serialize(files.Select(f => new { name = f.Name, size = f.Size }));

    private string[] Saved() =>
        Directory.Exists(_dir)
            ? Directory.GetFileSystemEntries(_dir).Select(p => Path.GetFileName(p)).OrderBy(n => n, StringComparer.Ordinal).ToArray()
            : [];

    // ---- the link ----------------------------------------------------------

    [Fact]
    public async Task AWrongLinkIsNotFoundAndNeverReachesThePrompt()
    {
        var asked = new List<PhoneUploadRequest>();
        Running page = Start(Answer(true, asked));

        using var stranger = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{page.Port}/") };

        HttpResponseMessage upload = await stranger.PostAsync(
            $"{PhonePageServer.NewToken()}/upload", Form(null, ("a.txt", "hello")));
        HttpResponseMessage root = await stranger.GetAsync("");
        HttpResponseMessage info = await stranger.GetAsync("info");

        Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, info.StatusCode);
        Assert.Empty(asked);
        Assert.Empty(Saved());
    }

    [Fact]
    public async Task ThePageIsServedWithItsSecurityHeaders()
    {
        Running page = Start(Answer(false));

        HttpResponseMessage response = await page.Client.GetAsync("");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<script src=\"page.js\" defer></script>", html);
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.True(response.Headers.CacheControl!.NoStore);

        Assert.Equal(HttpStatusCode.OK, (await page.Client.GetAsync("page.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await page.Client.GetAsync("page.css")).StatusCode);
    }

    [Fact]
    public async Task TheLinkWithoutItsTrailingSlashRedirects()
    {
        Running page = Start(Answer(false));
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{page.Port}/{page.Token}");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal($"/{page.Token}/", response.Headers.Location!.OriginalString);
    }

    // ---- uploads -----------------------------------------------------------

    [Fact]
    public async Task AnAcceptedUploadIsSavedUnderTheNamesThePersonSaw()
    {
        var asked = new List<PhoneUploadRequest>();
        Running page = Start(Answer(true, asked));

        // Safari has been known to send every photo as image.jpg; the page's list has the
        // real names, and the list is what the person agreed to.
        HttpResponseMessage response = await page.Client.PostAsync("upload", Form(
            Manifest(("IMG_0001.JPG", 5), ("notes.txt", 3)),
            ("image.jpg", "photo"), ("notes.txt", "abc")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["IMG_0001.JPG", "notes.txt"], Saved());
        Assert.Equal("photo", await File.ReadAllTextAsync(Path.Combine(_dir, "IMG_0001.JPG")));

        PhoneUploadRequest request = Assert.Single(asked);
        Assert.Equal("iPhone", request.Sender);
        Assert.Equal("127.0.0.1", request.RemoteAddress);
        Assert.Equal(["IMG_0001.JPG", "notes.txt"], request.Files.Select(f => f.Name));
        Assert.Equal(8, request.TotalBytes);
    }

    [Fact]
    public async Task ADeclinedUploadLeavesNothingBehind()
    {
        Running page = Start(Answer(false));

        HttpResponseMessage response = await page.Client.PostAsync("upload", Form(
            Manifest(("a.txt", 5)), ("a.txt", "hello")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(Saved());
    }

    [Fact]
    public async Task ThePersonIsAskedBeforeTheUploadHasArrived()
    {
        // Sent by hand: the head declares ten megabytes, and only the list and the first
        // file's headers follow. If the server read the upload before asking, it would be
        // stuck waiting for the rest and the question would never come.
        var asked = new TaskCompletionSource<PhoneUploadRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Running page = Start(async (request, _) =>
        {
            asked.TrySetResult(request);
            return await decision.Task;
        });

        const string boundary = "b0undary";
        string start =
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"manifest\"\r\n\r\n" +
            Manifest(("big.mov", 10_000_000)) + "\r\n" +
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"big.mov\"\r\n\r\n";

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, page.Port);
        NetworkStream stream = tcp.GetStream();

        string head =
            $"POST /{page.Token}/upload HTTP/1.1\r\nHost: test\r\n" +
            $"Content-Type: multipart/form-data; boundary={boundary}\r\n" +
            $"Content-Length: {start.Length + 10_000_000 + boundary.Length + 8}\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head + start));

        PhoneUploadRequest request = await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("big.mov", request.Files.Single().Name);
        Assert.Equal(10_000_000, request.TotalBytes);

        decision.SetResult(false);

        var reply = new byte[64];
        int read = await stream.ReadAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("HTTP/1.1 403", Encoding.ASCII.GetString(reply, 0, read));
        Assert.Empty(Saved());
    }

    [Fact]
    public async Task AFileLargerThanItsListingIsRefusedAndNothingIsKept()
    {
        Running page = Start(Answer(true));

        HttpResponseMessage response = await page.Client.PostAsync("upload", Form(
            Manifest(("first.txt", 3), ("tiny.jpg", 3)),
            ("first.txt", "abc"), ("tiny.jpg", "much more than three bytes")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Saved()); // not even first.txt, which was fine on its own
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public async Task AFileCountThatDoesNotMatchTheListingIsRefused(int listed, int sent)
    {
        Running page = Start(Answer(true));

        HttpResponseMessage response = await page.Client.PostAsync("upload", Form(
            Manifest(Enumerable.Range(0, listed).Select(i => ($"f{i}.txt", 1L)).ToArray()),
            Enumerable.Range(0, sent).Select(i => ($"f{i}.txt", "x")).ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Saved());
    }

    [Fact]
    public async Task AnUploadThatDoesNotListItsFilesIsRefusedWithoutAsking()
    {
        // The list is what the person is shown. Without one there is nothing honest to ask
        // about, so the question is never put.
        var asked = new List<PhoneUploadRequest>();
        Running page = Start(Answer(true, asked));

        HttpResponseMessage response = await page.Client.PostAsync("upload", Form(
            null, ("IMG_0002.HEIC", "one"), ("IMG_0003.HEIC", "two")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(asked);
        Assert.Empty(Saved());
    }

    [Fact]
    public async Task AnUploadOverTheLimitIsRefusedWithoutAsking()
    {
        var asked = new List<PhoneUploadRequest>();
        Running page = Start(Answer(true, asked), maxUploadBytes: 100);

        HttpResponseMessage response = await page.Client.PostAsync("upload", Form(
            null, ("big.bin", new string('x', 1000))));

        Assert.Equal((HttpStatusCode)413, response.StatusCode);
        Assert.Empty(asked);
        Assert.Empty(Saved());
    }

    [Fact]
    public async Task AnUploadWithNoFilesIsAnsweredWithoutAsking()
    {
        var asked = new List<PhoneUploadRequest>();
        Running page = Start(Answer(true, asked));

        var form = new MultipartFormDataContent { { new StringContent("iPhone"), "from" } };
        HttpResponseMessage response = await page.Client.PostAsync("upload", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task TwoWaysOfFramingABodyAreRefused()
    {
        Running page = Start(Answer(true));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, page.Port);
        NetworkStream stream = tcp.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"POST /{page.Token}/upload HTTP/1.1\r\nHost: test\r\nContent-Length: 5\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n"));

        var reply = new byte[64];
        int read = await stream.ReadAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("HTTP/1.1 400", Encoding.ASCII.GetString(reply, 0, read));
    }

    [Fact]
    public async Task AnIdleConnectionIsClosed()
    {
        Running page = Start(Answer(true), idle: TimeSpan.FromMilliseconds(300));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, page.Port);

        int read = await tcp.GetStream().ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, read);
    }

    // ---- downloads ---------------------------------------------------------

    [Fact]
    public async Task OfferedFilesAreListedAndDownloadable()
    {
        Running page = Start(Answer(false));

        string offered = Path.Combine(Path.GetTempPath(), $"windrop-offer-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(offered, "from the PC");

        try
        {
            page.Server.OfferFiles([offered, Path.Combine(_dir, "missing.txt")]);

            using JsonDocument info = JsonDocument.Parse(await page.Client.GetStringAsync("info"));
            Assert.Equal("TEST-PC", info.RootElement.GetProperty("name").GetString());

            JsonElement file = info.RootElement.GetProperty("offered").EnumerateArray().Single();
            Assert.Equal(Path.GetFileName(offered), file.GetProperty("name").GetString());

            string url = file.GetProperty("url").GetString()!;
            HttpResponseMessage download = await page.Client.GetAsync(url);

            Assert.Equal("from the PC", await download.Content.ReadAsStringAsync());
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);

            HttpResponseMessage head = await page.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
            Assert.Equal(11, head.Content.Headers.ContentLength);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());

            page.Server.WithdrawOffer();
            Assert.Equal(HttpStatusCode.NotFound, (await page.Client.GetAsync(url)).StatusCode);
        }
        finally
        {
            File.Delete(offered);
        }
    }

    // ---- names -------------------------------------------------------------

    [Theory]
    [InlineData("IMG_0001.JPG", "IMG_0001.JPG")]
    [InlineData("../../evil.txt", "evil.txt")]
    [InlineData("C:\\Windows\\evil.dll", "evil.dll")]
    [InlineData("a:b.txt", "a_b.txt")] // a colon would name an NTFS alternate data stream
    [InlineData("<>|?*\".txt", "______.txt")]
    [InlineData("photo\u202Egpj.exe", "photo_gpj.exe")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("com1", "_com1")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("trailing. . .", "trailing")]
    [InlineData("", "upload")]
    [InlineData("..", "upload")]
    public void UploadedNamesAreMadeSafeForWindows(string sent, string expected)
    {
        string root = Path.Combine(Path.GetTempPath(), "windrop-names");
        Assert.Equal(Path.Combine(root, expected), PhonePageServer.Destination(root, sent));
    }

    [Fact]
    public void AVeryLongNameKeepsItsExtension()
    {
        string root = Path.Combine(Path.GetTempPath(), "windrop-names");
        string name = Path.GetFileName(PhonePageServer.Destination(root, new string('a', 400) + ".jpg"));

        Assert.Equal(200, name.Length);
        Assert.EndsWith(".jpg", name);
    }

    [Theory]
    [InlineData("photo.jpg", "attachment; filename=\"photo.jpg\"; filename*=UTF-8''photo.jpg")]
    [InlineData("say \"hi\".txt", "attachment; filename=\"say _hi_.txt\"; filename*=UTF-8''say%20%22hi%22.txt")]
    [InlineData("写真.jpg", "attachment; filename=\"__.jpg\"; filename*=UTF-8''%E5%86%99%E7%9C%9F.jpg")]
    public void DownloadNamesSurviveTheHeader(string name, string expected)
    {
        Assert.Equal(expected, PhonePageServer.Attachment(name));
    }

    // ---- the console -------------------------------------------------------

    [Fact]
    public void TheConsolePromptCleansWhatThePhoneSent()
    {
        var request = new PhoneUploadRequest(
            "iPhone\u001b[2J", "192.0.2.7", [new PhoneUploadFile("evil\u202Etxt.exe", 1234)], TotalBytes: 1234);

        IReadOnlyList<string> lines = ConsoleText.DescribeUpload(request, autoAccepted: false);

        Assert.DoesNotContain(lines, line => line.Any(char.IsControl) || line.Contains('\u202E'));
        Assert.Contains(lines, line => line.Contains("evil?txt.exe"));
        Assert.Contains(lines, line => line.Contains("1,234 bytes in all"));
    }

    [Fact]
    public void TheConsoleCodeHasAQuietZoneAndSquareModules()
    {
        QrCode code = QrCode.Encode("http://192.0.2.7:8771/abcdefghijklmnopqrstuv/");
        IReadOnlyList<string> lines = ConsoleText.QrLines(code);

        int width = code.Size + 8;
        Assert.All(lines, line => Assert.Equal(width, line.Length));
        Assert.Equal((width + 1) / 2, lines.Count);

        // Four light modules all round: two full lines of blocks at the top.
        Assert.All(lines.Take(2), line => Assert.All(line, c => Assert.Equal('\u2588', c)));
    }
}
