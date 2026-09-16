using System.Diagnostics;
using WinDrop.Protocol.Archive;
using WinDrop.Protocol.Compression;
using WinDrop.Protocol.Discovery;
using WinDrop.Protocol.Http;
using WinDrop.Protocol.Plist;

namespace WinDrop.Protocol;

public sealed class AirDropReceiverOptions
{
    public string ComputerName { get; init; } = "WinDrop";
    public string ModelName { get; init; } = "Windows";
    public required string DownloadDirectory { get; init; }
    public AirDropReceiverFlags Flags { get; init; } = AirDropReceiverFlags.SupportsDvZip;

    /// <summary>
    /// Decides whether to accept a transfer. This is the security boundary of the whole
    /// protocol: TLS authenticates nobody, so a human saying yes is the only thing
    /// standing between an arbitrary peer on the link and the download directory.
    /// </summary>
    public required Func<AirDropAskRequest, CancellationToken, Task<bool>> ConsentHandler { get; init; }

    /// <summary>
    /// Receives one line per notable step of an upload: which encoding arrived, with its
    /// first bytes, and each archive member. Optional; the protocol behaves the same
    /// without it. It exists because some facts can only be observed during a real
    /// device's transfer. The first iPhone upload failed before anyone could tell which
    /// encoding it had used.
    /// </summary>
    public Action<string>? Log { get; init; }

    public long MaxUploadBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public int MaxAskBodyBytes { get; init; } = 1024 * 1024;
}

public sealed record AirDropTransferResult(IReadOnlyList<string> Files, long TotalBytes);

/// <summary>
/// The receiving half of AirDrop: /Discover, /Ask and /Upload over one TLS connection.
///
/// THE STATE MACHINE IS THE SECURITY MODEL. /Upload is refused unless /Ask was accepted
/// on this same connection. Without that rule a peer could skip straight to /Upload and
/// write files with no consent prompt at all, which is why the connection has to be
/// owned rather than handed to a pooling HTTP client that might move requests around.
/// </summary>
public sealed class AirDropReceiver(AirDropReceiverOptions options)
{
    private enum State { Fresh, Accepted, Completed }

    public AirDropReceiverFlags Flags => options.Flags;

    /// <summary>Serves one already-authenticated connection until the peer disconnects.</summary>
    public async Task<AirDropTransferResult?> HandleConnectionAsync(Stream tlsStream, CancellationToken ct = default)
    {
        await using var connection = new HttpConnection(tlsStream, ownsStream: false);

        var state = State.Fresh;
        AirDropAskRequest? accepted = null;
        AirDropTransferResult? result = null;

        while (await connection.ReadRequestHeadAsync(ct) is { } head)
        {
            // Compare on the path only; a peer may append a query string.
            string path = head.Target.Split('?', 2)[0].TrimEnd('/');

            // Logged as it arrives, not when the connection ends. A peer can keep the
            // connection open after a transfer, and a CLI that reports only at close then
            // looks idle while requests are still coming in.
            options.Log?.Invoke($"request {Printable(head.Method)} {Printable(path)} ({DescribeBody(head.Headers)})");

            switch (path)
            {
                case "/Discover":
                    await connection.ReadBodyAsync(head.Headers, options.MaxAskBodyBytes, ct);
                    options.Log?.Invoke("-> 200");
                    await RespondPlistAsync(connection, 200, "OK",
                        new AirDropReceiverIdentity(options.ComputerName, options.ModelName).ToPlist(), ct);
                    break;

                case "/Ask":
                {
                    // Timed because this round trip takes seconds over AWDL and sits right
                    // on the edge of iOS's patience: an /Ask answered too late shows on the
                    // phone as a decline. The parts are timed apart so the blame is visible
                    // — reading the body, which carries the preview image, then the consent
                    // decision, then writing the reply.
                    var askTimer = Stopwatch.StartNew();

                    byte[] body = await connection.ReadBodyAsync(head.Headers, options.MaxAskBodyBytes, ct);
                    long bodyMs = askTimer.ElapsedMilliseconds;

                    options.Log?.Invoke($"/Ask body: {body.Length:N0} bytes read in {bodyMs:N0} ms");

                    if (BinaryPlistReader.Parse(body) is not IReadOnlyDictionary<string, object?> plist)
                        throw new AirDropHttpException("/Ask body was not a plist dictionary.");

                    AirDropAskRequest request = AirDropAskRequest.FromPlist(plist);

                    long consentStart = askTimer.ElapsedMilliseconds;

                    if (await options.ConsentHandler(request, ct))
                    {
                        state = State.Accepted;
                        accepted = request;

                        long consentMs = askTimer.ElapsedMilliseconds - consentStart;

                        await RespondPlistAsync(connection, 200, "OK",
                            new AirDropReceiverIdentity(options.ComputerName, options.ModelName).ToPlist(), ct);

                        long replyMs = askTimer.ElapsedMilliseconds - consentStart - consentMs;

                        options.Log?.Invoke(
                            $"-> 200 accepted (body {bodyMs:N0} ms, consent {consentMs:N0} ms, reply {replyMs:N0} ms, total {askTimer.ElapsedMilliseconds:N0} ms)");
                    }
                    else
                    {
                        // Apple signals refusal with 401. It does not mean "authenticate
                        // and retry" here; it means the human said no.
                        state = State.Fresh;
                        accepted = null;
                        options.Log?.Invoke("-> 401 declined");
                        await connection.WriteResponseAsync(401, "Unauthorized", new HttpHeaders(), ReadOnlyMemory<byte>.Empty, ct);
                    }

                    break;
                }

                case "/Upload":
                {
                    if (state != State.Accepted || accepted is null)
                    {
                        // Named apart from a plain missing /Ask because it is an open question
                        // whether iOS sends a multi-item share as several uploads after one
                        // /Ask. If it does, this refusal is the bug, and the log should say so.
                        options.Log?.Invoke(state == State.Completed
                            ? "-> 401: a second /Upload on this connection, after one already completed"
                            : "-> 401: /Upload without an accepted /Ask on this connection");
                        // Drain so the connection stays framed, then refuse.
                        await using (Stream ignored = connection.OpenBody(head.Headers))
                            await ignored.CopyToAsync(Stream.Null, ct);

                        await connection.WriteResponseAsync(401, "Unauthorized", new HttpHeaders(), ReadOnlyMemory<byte>.Empty, ct);
                        break;
                    }

                    result = await ReceiveUploadAsync(connection, head.Headers, accepted, ct);
                    state = State.Completed;
                    options.Log?.Invoke($"upload complete: {result.Files.Count} file(s), {result.TotalBytes:N0} bytes -> 200");

                    await connection.WriteResponseAsync(200, "OK", new HttpHeaders(), ReadOnlyMemory<byte>.Empty, ct);
                    break;
                }

                default:
                    await connection.ReadBodyAsync(head.Headers, options.MaxAskBodyBytes, ct);
                    options.Log?.Invoke("-> 404");
                    await connection.WriteResponseAsync(404, "Not Found", new HttpHeaders(), ReadOnlyMemory<byte>.Empty, ct);
                    break;
            }
        }

        options.Log?.Invoke("connection closed by peer");
        return result;
    }

    private async Task<AirDropTransferResult> ReceiveUploadAsync(
        HttpConnection connection,
        HttpHeaders headers,
        AirDropAskRequest? consented,
        CancellationToken ct)
    {
        // The compression is NOT signalled in a header. opendrop sends only
        // "Content-Type: application/x-cpio" and gzips the body regardless, and an
        // earlier version of this code guessed from our own advertised capability
        // flags — which fed a gzip stream to the DVZip reader, whose first "block
        // length" came out as 0x1F8B0800, the gzip magic read as a big-endian int.
        //
        // All three encodings are self-identifying, so read the bytes instead of
        // guessing. Content-Encoding is still honoured when a peer bothers to send it,
        // but only as a cross-check we log, never as the deciding vote.
        await using Stream rawBody = connection.OpenBody(headers);
        var limited = new LimitedStream(rawBody, options.MaxUploadBytes);

        var prefix = new byte[6];
        int sniffed = 0;

        while (sniffed < prefix.Length)
        {
            int read = await limited.ReadAsync(prefix.AsMemory(sniffed), ct);
            if (read == 0) break;
            sniffed += read;
        }

        Stream body = new PrefixedStream(prefix.AsMemory(0, sniffed), limited);

        // gzip: 1f 8b. cpio newc: the ASCII magic "070701". Anything else is taken as
        // DVZip, whose frame begins with a length rather than a recognisable magic.
        bool isGzip = sniffed >= 2 && prefix[0] == 0x1F && prefix[1] == 0x8B;
        bool isRawCpio = sniffed >= 6
            && prefix[0] == (byte)'0' && prefix[1] == (byte)'7' && prefix[2] == (byte)'0'
            && prefix[3] == (byte)'7' && prefix[4] == (byte)'0' && prefix[5] == (byte)'1';

        // The first bytes settle the question beyond the classification. DVZip should show
        // a four-byte block length followed by a zlib header (78 xx).
        options.Log?.Invoke(
            $"upload: {(isRawCpio ? "raw cpio" : isGzip ? "gzip" : "dvzip")}, first bytes {Convert.ToHexString(prefix, 0, sniffed)}");

        var archive = new MemoryStream();

        if (isRawCpio)
            await body.CopyToAsync(archive, ct);
        else
            await AirDropCompression.DecompressAsync(body, archive, isDvZip: !isGzip, options.Log, ct);

        archive.Position = 0;

        return await ExtractAsync(archive, consented, ct);
    }

    /// <summary>Unpacks a decompressed cpio archive into the download directory.</summary>
    internal async Task<AirDropTransferResult> ExtractAsync(
        Stream archive,
        AirDropAskRequest? consented,
        CancellationToken ct)
    {
        Directory.CreateDirectory(options.DownloadDirectory);

        var reader = new CpioReader(archive);
        var written = new List<string>();
        long total = 0;

        while (await reader.ReadNextAsync(ct) is { } entry)
        {
            options.Log?.Invoke(entry.IsDirectory
                ? $"member {Printable(entry.Name)} (directory)"
                : $"member {Printable(entry.Name)} ({entry.Size:N0} bytes)");

            // iOS 26.6 opens its upload archive with a directory member named ".": the root
            // the archive was built from (observed 2026-09-13). That is the download
            // directory itself, so there is nothing to create. Refusing it, as the first
            // real upload did, failed every transfer from an iPhone. Only a *directory*
            // gets this pass. A file claiming to be the root still reaches ResolveSafePath
            // and is refused there, which is the guard doing its job.
            if (entry.IsDirectory && IsArchiveRoot(entry.Name)) continue;

            // Consent covers the names listed in /Ask. Apple's own archives also carry
            // members outside that list, the root skipped above among them, so a mismatch
            // is reported rather than refused. Reported, though: a person agreeing to
            // these files is the only thing standing behind writing them.
            if (consented is not null && !IsConsented(consented, entry.Name))
                options.Log?.Invoke($"member {Printable(entry.Name)} was not in the accepted /Ask list");

            string destination = ResolveSafePath(options.DownloadDirectory, entry.Name);

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // Two members can name the same file: iOS names every edited photo
            // FullSizeRender.heic. Overwriting would hand over one file where the user
            // accepted three, and say nothing about it.
            string free = Unused(destination);

            if (free != destination)
            {
                options.Log?.Invoke(
                    $"member {Printable(entry.Name)} saved as {Path.GetFileName(free)}; that name was taken");

                destination = free;
            }

            byte[] content = await reader.ReadContentAsync(ct);
            await File.WriteAllBytesAsync(destination, content, ct);

            written.Add(destination);
            total += content.Length;
        }

        return new AirDropTransferResult(written, total);
    }

    /// <summary>
    /// True for a member that names the archive root: ".", "./", "./.", and the like. An
    /// absolute "/" is not the root in this sense. It is an absolute path, and it is left
    /// for ResolveSafePath to refuse.
    /// </summary>
    internal static bool IsArchiveRoot(string name)
    {
        string relative = name.Replace('\\', '/');
        if (relative.StartsWith('/')) return false;

        while (relative.StartsWith("./", StringComparison.Ordinal))
            relative = relative[2..];

        return relative.TrimEnd('/') is "" or ".";
    }

    /// <summary>
    /// Member names come from an unauthenticated peer and end up in a terminal. Control
    /// characters are replaced, so a name cannot carry escape sequences to whoever reads
    /// the log.
    /// </summary>
    internal static string Printable(string text) =>
        new(text.Select(c => char.IsControl(c) ? '?' : c).ToArray());

    private static string DescribeBody(HttpHeaders headers) =>
        headers.IsChunked ? "chunked"
        : headers.ContentLength is { } length ? $"{length:N0} bytes"
        : "no body";

    /// <summary>Was this member among the files the user accepted?</summary>
    private static bool IsConsented(AirDropAskRequest request, string memberName)
    {
        string normalised = memberName.Replace('\\', '/');

        return request.Files.Any(file =>
            string.Equals(file.FileBomPath, normalised, StringComparison.Ordinal)
            || string.Equals(file.FileName, Path.GetFileName(normalised), StringComparison.Ordinal));
    }

    /// <summary>
    /// The given path, or the first free "name (n).ext" beside it, so a second member of
    /// the same name cannot replace the first.
    /// </summary>
    private static string Unused(string path)
    {
        if (!File.Exists(path)) return path;

        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        for (int n = 2; n < 10_000; n++)
        {
            string candidate = Path.Combine(directory, $"{name} ({n}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        throw new AirDropHttpException($"Too many members named like '{Path.GetFileName(path)}'.");
    }

    /// <summary>
    /// Maps an archive member name onto a path inside the download directory, refusing
    /// anything that would escape it.
    ///
    /// The member names come from an unauthenticated peer, so this is the boundary that
    /// stops a transfer the user accepted for "photo.jpg" from also dropping a file into
    /// their startup folder. Checking for ".." by string match is not sufficient — the
    /// only reliable test is to resolve the full path and confirm it is still under the
    /// root.
    /// </summary>
    internal static string ResolveSafePath(string root, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
            throw new AirDropHttpException("Archive member has an empty name.");

        if (entryName.Contains('\0'))
            throw new AirDropHttpException("Archive member name contains a NUL.");

        string relative = entryName.Replace('\\', '/');

        while (relative.StartsWith("./", StringComparison.Ordinal))
            relative = relative[2..];

        if (relative.Length == 0)
            throw new AirDropHttpException("Archive member resolves to the root directory.");

        if (Path.IsPathRooted(relative) || relative.StartsWith('/'))
            throw new AirDropHttpException($"Archive member '{entryName}' is an absolute path.");

        // A Windows drive-relative name like "C:evil" is not caught by IsPathRooted.
        if (relative.Length >= 2 && relative[1] == ':')
            throw new AirDropHttpException($"Archive member '{entryName}' names a drive.");

        string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string candidate = Path.GetFullPath(Path.Combine(rootFull, relative));

        if (!candidate.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new AirDropHttpException($"Archive member '{entryName}' escapes the download directory.");
        }

        if (string.Equals(candidate, rootFull, StringComparison.OrdinalIgnoreCase))
            throw new AirDropHttpException($"Archive member '{entryName}' resolves to the download directory itself.");

        return candidate;
    }

    private static async Task RespondPlistAsync(
        HttpConnection connection,
        int status,
        string reason,
        Dictionary<string, object?> payload,
        CancellationToken ct)
    {
        var headers = new HttpHeaders();
        headers.Set("Content-Type", "application/octet-stream");

        await connection.WriteResponseAsync(status, reason, headers, BinaryPlistWriter.Write(payload), ct);
    }
}

/// <summary>Caps how many bytes a peer can push before we give up on the transfer.</summary>
internal sealed class LimitedStream(Stream inner, long limit) : Stream
{
    private long _read;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int read = await inner.ReadAsync(buffer, ct);
        _read += read;

        if (_read > limit)
            throw new AirDropHttpException($"Upload exceeded the {limit} byte limit.");

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Replays a handful of bytes already read from a stream, then continues with the rest.
/// Needed because identifying the upload encoding means consuming its first bytes, and
/// the decompressor still has to see them.
/// </summary>
internal sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream rest) : Stream
{
    private int _offset;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_offset < prefix.Length)
        {
            int take = Math.Min(buffer.Length, prefix.Length - _offset);
            prefix.Slice(_offset, take).CopyTo(buffer);
            _offset += take;
            return take;
        }

        return await rest.ReadAsync(buffer, ct);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
