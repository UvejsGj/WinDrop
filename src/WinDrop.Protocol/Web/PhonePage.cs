using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinDrop.Protocol.Http;

namespace WinDrop.Protocol.Web;

public sealed class PhonePageOptions
{
    public required string DownloadDirectory { get; init; }

    /// <summary>
    /// The secret in the page's address, from <see cref="PhonePageServer.NewToken"/>. Every
    /// request must carry it as the first path segment; anything else is answered 404, the
    /// same as a page that does not exist, so a stranger on the network learns nothing.
    /// </summary>
    public required string Token { get; init; }

    public string ComputerName { get; init; } = Environment.MachineName;

    /// <summary>
    /// Decides whether to accept an upload, before any of its files are read. As with
    /// AirDrop, this is the security boundary: the link keeps strangers from reaching the
    /// prompt at all, and the prompt keeps anyone who has the link from writing files.
    /// </summary>
    public required Func<PhoneUploadRequest, CancellationToken, Task<bool>> ConsentHandler { get; init; }

    public Action<string>? Log { get; init; }

    /// <summary>Called after an upload is saved.</summary>
    public Action<PhoneUploadRequest, AirDropTransferResult>? Saved { get; init; }

    public long MaxUploadBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public int MaxFilesPerUpload { get; init; } = 1_000;
    public int MaxConnections { get; init; } = 16;

    /// <summary>How long a connection may sit with nothing moving before it is closed.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>One file in an upload, as the page lists it.</summary>
public sealed record PhoneUploadFile(string Name, long Size);

/// <summary>
/// What the person at the PC is asked about: every file the upload holds, by the page's own
/// list, which the upload is then held to. Every text field here came from the phone.
/// </summary>
public sealed record PhoneUploadRequest(
    string Sender,
    string RemoteAddress,
    IReadOnlyList<PhoneUploadFile> Files,
    long TotalBytes);

/// <summary>A file on this PC offered for download on the page.</summary>
public sealed record PhoneOfferedFile(string Path, string Name, long Size);

/// <summary>
/// The phone page: a small web server on the local network that a phone's browser opens
/// by scanning a QR code, to send files to this PC and download files from it. It is the
/// route to an iPhone that needs no AWDL, no Linux and no extra hardware, at ordinary Wi-Fi
/// speed. It is not AirDrop: the PC does not appear in the iPhone's AirDrop list, and the
/// phone uses the page instead. See docs/phone-page.md.
///
/// PLAIN HTTP, ON PURPOSE. Safari shows a full-page warning for a self-signed certificate,
/// so HTTPS would mean teaching people to click through certificate warnings. The traffic is protected by the Wi-Fi's own
/// encryption from anyone outside the network, not from someone already on it who can
/// intercept traffic, which is why this is meant for home and other trusted networks.
///
/// THE TWO LOCKS. The secret token in the address keeps everyone else on the network from
/// reaching the page, the prompt or the offered files. The consent prompt keeps anyone who
/// has the address from writing files: like AirDrop, an upload is asked about before any
/// file in it is read, and is held back by TCP while the person decides.
/// </summary>
public sealed class PhonePageServer(PhonePageOptions options)
{
    /// <summary>Next to AirDrop's 8770.</summary>
    public const int DefaultPort = 8771;

    private const int MaxFieldBytes = 64 * 1024;
    private const int MaxIgnoredBodyBytes = 64 * 1024;

    private sealed record Offer(int Generation, IReadOnlyList<PhoneOfferedFile> Files);

    private readonly SemaphoreSlim _connections = new(options.MaxConnections, options.MaxConnections);
    private readonly byte[] _token = Encoding.UTF8.GetBytes(options.Token);
    private Offer _offer = new(0, []);
    private int _generation;
    private TcpListener? _listener;

    /// <summary>128 random bits, base64url: unguessable, and safe in a URL as it stands.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The page's address on a given host. The token is in it, so it is shown only to the PC's owner.</summary>
    public static string PageUrl(string host, int port, string token) =>
        $"http://{(host.Contains(':') ? $"[{host}]" : host)}:{port}/{token}/";

    // ---- offering files ----------------------------------------------------

    /// <summary>
    /// Offers these files for download, replacing any earlier offer. Folders and missing
    /// paths are left out. Links to an earlier offer stop working, so a page left open on a
    /// phone cannot fetch a file that is no longer offered.
    /// </summary>
    public IReadOnlyList<PhoneOfferedFile> OfferFiles(IEnumerable<string> paths)
    {
        var files = paths
            .Where(File.Exists)
            .Select(path => new FileInfo(path))
            .Select(info => new PhoneOfferedFile(info.FullName, info.Name, info.Length))
            .ToList();

        _offer = new Offer(Interlocked.Increment(ref _generation), files);
        return files;
    }

    public void WithdrawOffer() => _offer = new Offer(Interlocked.Increment(ref _generation), []);

    // ---- listening ---------------------------------------------------------

    /// <summary>
    /// Starts listening on every interface, on <paramref name="port"/> or, if that is taken,
    /// on any free port. Returns the port. A fixed port is preferred so that a Windows
    /// Firewall rule someone writes for it keeps matching.
    /// </summary>
    public int Listen(int port = DefaultPort)
    {
        TcpListener listener;

        try
        {
            listener = Bind(port);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && port != 0)
        {
            options.Log?.Invoke($"phone page: port {port} is in use, taking another");
            listener = Bind(0);
        }

        _listener = listener;
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static TcpListener Bind(int port)
    {
        try
        {
            var dual = new TcpListener(IPAddress.IPv6Any, port);
            dual.Server.DualMode = true;
            dual.Start();
            return dual;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
        {
            var v4 = new TcpListener(IPAddress.Any, port);
            v4.Start();
            return v4;
        }
    }

    /// <summary>Serves connections until cancelled. Call <see cref="Listen"/> first.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        TcpListener listener = _listener ?? throw new InvalidOperationException("Call Listen first.");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(ct);

                // Refused at once rather than queued: more connections than a phone's browser
                // would ever open is someone else, and waiting would let them hold the queue.
                if (!_connections.Wait(0))
                {
                    client.Dispose();
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (client)
                            await ServeAsync(client.GetStream(), Describe(client.Client.RemoteEndPoint), ct);
                    }
                    finally
                    {
                        _connections.Release();
                    }
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            listener.Stop();
        }
    }

    private static string Describe(EndPoint? endPoint) => endPoint switch
    {
        IPEndPoint ip when ip.Address.IsIPv4MappedToIPv6 => ip.Address.MapToIPv4().ToString(),
        IPEndPoint ip => ip.Address.ToString(),
        _ => "unknown",
    };

    // ---- one connection ----------------------------------------------------

    /// <summary>Serves one connection until the peer closes it, it idles out, or a request ends it.</summary>
    public async Task ServeAsync(Stream stream, string remote, CancellationToken ct)
    {
        var timed = new IdleTimeoutStream(stream, options.IdleTimeout);
        await using var connection = new HttpConnection(timed, ownsStream: false);

        try
        {
            while (await connection.ReadRequestHeadAsync(ct) is { } head)
            {
                if (!await HandleAsync(connection, head, remote, ct)) break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Browsers keep idle connections open and drop them without ceremony, so a
            // timeout or a reset between requests is routine. Anything else is worth a line.
            if (ex is not (TimeoutException or IOException))
                options.Log?.Invoke($"phone page: connection from {remote} ended: {PeerText.Printable(ex.Message)}");
        }
    }

    /// <summary>Answers one request. False when the connection must close after it.</summary>
    private async Task<bool> HandleAsync(HttpConnection connection, HttpRequestHead head, string remote, CancellationToken ct)
    {
        // Two ways to say where a body ends is how request smuggling starts. So is a
        // Transfer-Encoding other than plain chunked, which HttpConnection would ignore.
        string? transferEncoding = head.Headers["Transfer-Encoding"];
        if ((transferEncoding is not null && !head.Headers.IsChunked)
            || (transferEncoding is not null && head.Headers.Contains("Content-Length")))
        {
            await RespondTextAsync(connection, 400, "Bad Request", "Bad request.", close: true, ct);
            return false;
        }

        string path = head.Target.Split('?', 2)[0];
        string[] segments = path.Split('/');

        // "/<token>/..." splits into "", the token, then the rest.
        if (segments.Length < 2 || segments[0].Length != 0 || !IsToken(segments[1]))
        {
            if (!await SkipBodyAsync(connection, head, ct)) return false;

            // Not logged per request: a browser asks for /favicon.ico on its own, and a
            // stranger probing the port should not be able to fill the log.
            await RespondTextAsync(connection, 404, "Not Found", "Not found.", close: false, ct);
            return true;
        }

        string route = string.Join('/', segments.Skip(2));

        if (head.Method == "POST" && route == "upload")
            return await UploadAsync(connection, head, remote, ct);

        if (!await SkipBodyAsync(connection, head, ct)) return false;

        bool isHead = head.Method == "HEAD";
        if (head.Method != "GET" && !isHead)
        {
            await RespondTextAsync(connection, 405, "Method Not Allowed", "Method not allowed.", close: false, ct);
            return true;
        }

        switch (route)
        {
            case "" when segments.Length == 2:
                // "/<token>" without the slash: the page's relative links need it.
                var redirect = new HttpHeaders().Set("Location", $"/{options.Token}/");
                await connection.WriteResponseAsync(301, "Moved Permanently", Secured(redirect), ReadOnlyMemory<byte>.Empty, ct);
                return true;

            case "":
                options.Log?.Invoke($"phone page: opened from {remote}");
                await RespondAssetAsync(connection, "page.html", "text/html; charset=utf-8", isHead, ct);
                return true;

            case "page.css":
                await RespondAssetAsync(connection, "page.css", "text/css; charset=utf-8", isHead, ct);
                return true;

            case "page.js":
                await RespondAssetAsync(connection, "page.js", "text/javascript; charset=utf-8", isHead, ct);
                return true;

            case "info":
                await RespondJsonAsync(connection, 200, "OK", Info(), close: false, ct);
                return true;
        }

        if (segments.Length == 5 && segments[2] == "files")
            return await DownloadAsync(connection, segments[3], segments[4], isHead, remote, ct);

        await RespondTextAsync(connection, 404, "Not Found", "Not found.", close: false, ct);
        return true;
    }

    /// <summary>Compared in constant time, so response timing does not leak how much of a guess was right.</summary>
    private bool IsToken(string candidate) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), _token);

    private object Info()
    {
        Offer offer = _offer;

        return new
        {
            name = options.ComputerName,
            offered = offer.Files.Select((file, index) => new
            {
                url = $"files/{offer.Generation}/{index}",
                name = file.Name,
                size = file.Size,
            }),
        };
    }

    // ---- receiving ---------------------------------------------------------

    /// <summary>
    /// One upload: a form whose text fields come first (who is sending, and the page's list
    /// of files), then the files. An upload without the list is refused unasked: the list is
    /// what the person is shown, so without one there is nothing honest to ask about.
    ///
    /// ASKED BEFORE ANY FILE IS READ. The text fields and the first file's headers are read,
    /// then the person is asked, and only after a yes is a byte of file content taken off the
    /// connection. Until then the upload waits in the socket and TCP holds the phone back:
    /// the same rule as AirDrop's early-ask, for the same reason. Reading first would let
    /// anyone with the link make this PC take in a file of any size before anyone agreed.
    /// </summary>
    private async Task<bool> UploadAsync(HttpConnection connection, HttpRequestHead head, string remote, CancellationToken ct)
    {
        string? boundary = MultipartReader.BoundaryFrom(head.Headers["Content-Type"]);
        if (boundary is null)
        {
            await RespondErrorAsync(connection, 415, "Unsupported Media Type", "Send the files as a form.", ct);
            return false;
        }

        long? declared = head.Headers.IsChunked ? null : head.Headers.ContentLength;

        if (declared is null && !head.Headers.IsChunked)
        {
            await RespondErrorAsync(connection, 411, "Length Required", "The upload has no length.", ct);
            return false;
        }

        if (declared > options.MaxUploadBytes)
        {
            options.Log?.Invoke($"phone page: refused an upload of {declared:N0} bytes from {remote}, over the limit");
            await RespondErrorAsync(connection, 413, "Content Too Large", "That is more than this PC accepts at once.", ct);
            return false;
        }

        await using Stream body = connection.OpenBody(head.Headers);
        var limited = new LimitedStream(body, options.MaxUploadBytes);
        var reader = new MultipartReader(limited, boundary);

        PhoneUploadRequest request;
        AirDropTransferResult result;

        try
        {
            (request, FormPart? first) = await ReadUntilFirstFileAsync(reader, remote, ct);

            if (first is null)
            {
                await limited.CopyToAsync(Stream.Null, ct);
                await RespondJsonAsync(connection, 400, "Bad Request", new { error = "no files" }, close: false, ct);
                return true;
            }

            options.Log?.Invoke(
                $"phone page: upload from {remote}: {Summary(request)}");

            if (!await AskAsync(request, ct))
            {
                options.Log?.Invoke("phone page: declined; no file content was read");
                await RespondJsonAsync(connection, 403, "Forbidden", new { error = "declined" }, close: true, ct);
                await LingerAsync(limited);
                return false;
            }

            result = await SaveAsync(reader, first, request.Files, ct);
            await limited.CopyToAsync(Stream.Null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            options.Log?.Invoke($"phone page: upload from {remote} failed, nothing saved: {PeerText.Printable(ex.Message)}");

            try
            {
                await RespondJsonAsync(connection, 400, "Bad Request", new { error = "failed" }, close: true, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // The phone is likely gone already; there is nobody to tell.
            }

            return false;
        }

        options.Log?.Invoke($"phone page: saved {result.Files.Count} file(s), {result.TotalBytes:N0} bytes");
        options.Saved?.Invoke(request, result);

        await RespondJsonAsync(connection, 200, "OK", new { saved = result.Files.Count }, close: false, ct);
        return true;
    }

    private async Task<(PhoneUploadRequest, FormPart?)> ReadUntilFirstFileAsync(
        MultipartReader reader, string remote, CancellationToken ct)
    {
        string sender = "";
        IReadOnlyList<PhoneUploadFile>? manifest = null;

        while (await reader.ReadNextPartAsync(ct) is { } part)
        {
            if (part.FileName is null)
            {
                string value = await ReadFieldAsync(part.Body, ct);

                if (part.Name == "from") sender = value;
                else if (part.Name == "manifest") manifest = ParseManifest(value);

                continue;
            }

            // A file input with nothing chosen still sends a part, with an empty name.
            if (part.FileName.Length == 0) continue;

            if (manifest is null)
                throw new AirDropHttpException("The upload did not list its files first.");

            return (new PhoneUploadRequest(sender, remote, manifest, manifest.Sum(f => f.Size)), part);
        }

        return (new PhoneUploadRequest(sender, remote, [], 0), null);
    }

    /// <summary>
    /// The page's list of what it is about to send: a JSON array of {name, size}. It is what
    /// the person is shown, so it is also what the files are held to.
    /// </summary>
    private IReadOnlyList<PhoneUploadFile> ParseManifest(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });

            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new AirDropHttpException("The list of files is not a list.");

            var files = new List<PhoneUploadFile>();

            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                if (files.Count >= options.MaxFilesPerUpload)
                    throw new AirDropHttpException($"More than {options.MaxFilesPerUpload:N0} files in one upload.");

                string name = entry.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()!
                    : throw new AirDropHttpException("A listed file has no name.");

                long size = entry.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long value) && value >= 0
                    ? value
                    : throw new AirDropHttpException("A listed file has no size.");

                if (name.Length is 0 or > 1024)
                    throw new AirDropHttpException("A listed file has an unusable name.");

                files.Add(new PhoneUploadFile(name, size));
            }

            return files;
        }
        catch (JsonException)
        {
            throw new AirDropHttpException("The list of files is not valid JSON.");
        }
    }

    /// <summary>Runs the consent handler, turning any failure into a refusal.</summary>
    private async Task<bool> AskAsync(PhoneUploadRequest request, CancellationToken ct)
    {
        try
        {
            return await options.ConsentHandler(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            options.Log?.Invoke($"phone page: consent failed, treating as declined: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Saves the files, all or nothing, through the same staging as AirDrop uploads.
    ///
    /// Each file is saved under the name the list gave it and may be no larger than the list
    /// said, and the count must match: what the person agreed to is what lands on disk, so a
    /// page cannot show "photo.jpg" at the prompt and deliver "photo.exe".
    /// </summary>
    private async Task<AirDropTransferResult> SaveAsync(
        MultipartReader reader, FormPart first, IReadOnlyList<PhoneUploadFile> manifest, CancellationToken ct)
    {
        Directory.CreateDirectory(options.DownloadDirectory);
        using var incoming = new IncomingFiles(options.DownloadDirectory, options.Log);

        FormPart? part = first;
        int index = 0;

        while (part is not null)
        {
            if (part.FileName is { Length: > 0 } sentName)
            {
                if (index >= options.MaxFilesPerUpload)
                    throw new AirDropHttpException($"More than {options.MaxFilesPerUpload:N0} files in one upload.");

                if (index >= manifest.Count)
                    throw new AirDropHttpException("The upload holds more files than it listed.");

                string name = manifest[index].Name;
                long limit = manifest[index].Size;

                if (name != sentName)
                    options.Log?.Invoke($"phone page: file {PeerText.Printable(sentName)} saved under its listed name {PeerText.Printable(name)}");

                string destination = Destination(options.DownloadDirectory, name);
                options.Log?.Invoke($"phone page: receiving {PeerText.Printable(Path.GetFileName(destination))}");

                FormPart current = part;
                await incoming.AddFileAsync(name, destination,
                    file => new LimitedStream(current.Body, limit, $"File {index + 1}").CopyToAsync(file, ct));

                index++;
            }

            part = await reader.ReadNextPartAsync(ct);
        }

        if (index != manifest.Count)
            throw new AirDropHttpException($"The upload listed {manifest.Count} files and held {index}.");

        return incoming.Commit();
    }

    /// <summary>
    /// Where an uploaded file goes. A form sends a bare name, but nothing stops a client
    /// sending a path, so only the last segment is kept, and it is made safe for Windows as
    /// well as for display: control and direction characters, the characters Windows
    /// forbids in a name (a colon would otherwise name an NTFS alternate data stream), the
    /// trailing dots and spaces Windows strips, and device names like CON. Then the same
    /// containment check as an AirDrop archive member.
    /// </summary>
    internal static string Destination(string root, string fileName)
    {
        string name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = AirDropReceiver.SafeName(name);

        name = new string(name.Select(c => c is '<' or '>' or ':' or '"' or '|' or '?' or '*' ? '_' : c).ToArray());
        name = name.Trim().TrimEnd('.', ' ');

        if (name.Length == 0 || name is "." or "..") name = "upload";

        string stem = name.Split('.')[0];
        if (IsDeviceName(stem)) name = "_" + name;

        if (name.Length > 200)
        {
            string extension = Path.GetExtension(name);
            if (extension.Length > 20) extension = "";
            name = name[..(200 - extension.Length)] + extension;
        }

        return AirDropReceiver.ResolveSafePath(root, name);
    }

    private static bool IsDeviceName(string stem)
    {
        string upper = stem.TrimEnd(' ').ToUpperInvariant();

        return upper is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
                && upper[3] is >= '0' and <= '9' or '¹' or '²' or '³');
    }

    private static async Task<string> ReadFieldAsync(Stream body, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];

        while (true)
        {
            int read = await body.ReadAsync(chunk, ct);
            if (read == 0) break;

            if (buffer.Length + read > MaxFieldBytes)
                throw new AirDropHttpException($"A form field is over {MaxFieldBytes:N0} bytes.");

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string Summary(PhoneUploadRequest request)
    {
        string names = string.Join(", ", request.Files.Take(5).Select(f => PeerText.Printable(f.Name)));
        if (request.Files.Count > 5) names += $" and {request.Files.Count - 5} more";

        string sender = request.Sender.Length > 0 ? $" as {PeerText.Printable(request.Sender)}" : "";

        return $"{names}, {request.TotalBytes:N0} bytes{sender}";
    }

    /// <summary>
    /// After refusing an upload that is still arriving, reads and discards what comes for a
    /// moment before the connection closes. Closing with unread data makes the PC reset the
    /// connection, and a reset can overtake the refusal, so the phone shows a network error
    /// instead of "declined". Bounded in time and bytes; nothing read here is kept.
    /// </summary>
    private static async Task LingerAsync(Stream body)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        byte[] discard = new byte[64 * 1024];
        long total = 0;

        try
        {
            while (total < 8 * 1024 * 1024)
            {
                int read = await body.ReadAsync(discard, timeout.Token);
                if (read == 0) break;
                total += read;
            }
        }
        catch (Exception)
        {
            // Whatever ends the linger ends it; the refusal has been sent either way.
        }
    }

    // ---- sending -----------------------------------------------------------

    private async Task<bool> DownloadAsync(
        HttpConnection connection, string generationText, string indexText, bool isHead, string remote, CancellationToken ct)
    {
        Offer offer = _offer;

        bool found = int.TryParse(generationText, NumberStyles.None, CultureInfo.InvariantCulture, out int generation)
            & int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out int index);

        if (!found || generation != offer.Generation || index < 0 || index >= offer.Files.Count)
        {
            await RespondTextAsync(connection, 404, "Not Found", "That file is no longer offered.", close: false, ct);
            return true;
        }

        PhoneOfferedFile offered = offer.Files[index];
        FileStream file;

        try
        {
            file = new FileStream(offered.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            options.Log?.Invoke($"phone page: could not open {offered.Name}: {ex.Message}");
            await RespondTextAsync(connection, 404, "Not Found", "That file could not be opened.", close: false, ct);
            return true;
        }

        await using (file)
        {
            long length = file.Length;

            var headers = new HttpHeaders()
                .Set("Content-Type", ContentTypeFor(offered.Name))
                .Set("Content-Disposition", Attachment(offered.Name));

            if (!isHead) options.Log?.Invoke($"phone page: sending {offered.Name} ({length:N0} bytes) to {remote}");

            // Exactly the declared length, even if the file grows meanwhile: the length is
            // already on the wire, and one byte more would desynchronise the connection.
            await connection.WriteStreamingResponseAsync(200, "OK", Secured(headers), length,
                isHead ? null : (stream, token) => CopyExactlyAsync(file, stream, length, token), ct);
        }

        return true;
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, long length, CancellationToken ct)
    {
        byte[] buffer = new byte[81920];
        long remaining = length;

        while (remaining > 0)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0) throw new IOException("The file shrank while it was being sent.");

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }
    }

    /// <summary>
    /// An attachment header that survives any name: a plain-ASCII fallback, and the real
    /// name percent-encoded as UTF-8 (RFC 6266 and 8187), which Safari prefers.
    /// </summary>
    internal static string Attachment(string name)
    {
        string fallback = new(name.Select(c => c is >= ' ' and <= '~' and not '"' and not '\\' and not '%' ? c : '_').ToArray());
        return $"attachment; filename=\"{fallback}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
    }

    private static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".heic" => "image/heic",
        ".webp" => "image/webp",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain; charset=utf-8",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    // ---- responses ---------------------------------------------------------

    /// <summary>
    /// Headers every response carries. The token is in the address, so nothing may leak it
    /// onward (Referrer-Policy) or keep a copy (Cache-Control), and the page may not be
    /// framed by another site or have its types second-guessed.
    /// </summary>
    private static HttpHeaders Secured(HttpHeaders headers) => headers
        .Set("Cache-Control", "no-store")
        .Set("Referrer-Policy", "no-referrer")
        .Set("X-Content-Type-Options", "nosniff")
        .Set("X-Frame-Options", "DENY");

    private const string PageSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";

    private static async Task RespondAssetAsync(HttpConnection connection, string name, string contentType, bool isHead, CancellationToken ct)
    {
        byte[] content = Asset(name);

        var headers = Secured(new HttpHeaders())
            .Set("Content-Type", contentType)
            .Set("Content-Security-Policy", PageSecurityPolicy);

        if (isHead)
            await connection.WriteStreamingResponseAsync(200, "OK", headers, content.Length, null, ct);
        else
            await connection.WriteResponseAsync(200, "OK", headers, content, ct);
    }

    private static async Task RespondJsonAsync(HttpConnection connection, int status, string reason, object value, bool close, CancellationToken ct)
    {
        var headers = Secured(new HttpHeaders()).Set("Content-Type", "application/json; charset=utf-8");
        if (close) headers.Set("Connection", "close");

        await connection.WriteResponseAsync(status, reason, headers, JsonSerializer.SerializeToUtf8Bytes(value), ct);
    }

    private static async Task RespondTextAsync(HttpConnection connection, int status, string reason, string text, bool close, CancellationToken ct)
    {
        var headers = Secured(new HttpHeaders()).Set("Content-Type", "text/plain; charset=utf-8");
        if (close) headers.Set("Connection", "close");

        await connection.WriteResponseAsync(status, reason, headers, Encoding.UTF8.GetBytes(text), ct);
    }

    private static Task RespondErrorAsync(HttpConnection connection, int status, string reason, string message, CancellationToken ct) =>
        RespondJsonAsync(connection, status, reason, new { error = message }, close: true, ct);

    /// <summary>
    /// Reads and drops a body on a request that should not have one, so the connection stays
    /// framed. False, after an answer, when it is too large to bother with.
    /// </summary>
    private static async Task<bool> SkipBodyAsync(HttpConnection connection, HttpRequestHead head, CancellationToken ct)
    {
        if (!head.Headers.IsChunked && (head.Headers.ContentLength ?? 0) == 0) return true;

        try
        {
            await connection.ReadBodyAsync(head.Headers, MaxIgnoredBodyBytes, ct);
            return true;
        }
        catch (AirDropHttpException)
        {
            await RespondTextAsync(connection, 413, "Content Too Large", "Too large.", close: true, ct);
            return false;
        }
    }

    private static readonly Dictionary<string, byte[]> Assets = new();

    private static byte[] Asset(string name)
    {
        lock (Assets)
        {
            if (Assets.TryGetValue(name, out byte[]? cached)) return cached;

            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"WinDrop.Protocol.Web.{name}")
                ?? throw new InvalidOperationException($"The page asset {name} is missing from the build.");

            var content = new byte[stream.Length];
            stream.ReadExactly(content);
            Assets[name] = content;
            return content;
        }
    }
}

/// <summary>
/// Gives up on a read or write that makes no progress for too long. A browser keeps idle
/// connections open, and a phone that walks out of Wi-Fi range mid-transfer never closes
/// its side, so without this a connection could hold a slot forever.
/// </summary>
internal sealed class IdleTimeoutStream(Stream inner, TimeSpan idle) : Stream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(idle);

        try
        {
            return await inner.ReadAsync(buffer, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Nothing arrived for {idle.TotalSeconds:N0} s.");
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(idle);

        try
        {
            await inner.WriteAsync(buffer, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Nothing could be sent for {idle.TotalSeconds:N0} s.");
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override void Flush() => inner.Flush();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
