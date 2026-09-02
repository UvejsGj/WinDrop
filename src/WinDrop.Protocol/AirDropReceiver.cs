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

            switch (path)
            {
                case "/Discover":
                    await connection.ReadBodyAsync(head.Headers, options.MaxAskBodyBytes, ct);
                    await RespondPlistAsync(connection, 200, "OK",
                        new AirDropReceiverIdentity(options.ComputerName, options.ModelName).ToPlist(), ct);
                    break;

                case "/Ask":
                {
                    byte[] body = await connection.ReadBodyAsync(head.Headers, options.MaxAskBodyBytes, ct);

                    if (BinaryPlistReader.Parse(body) is not IReadOnlyDictionary<string, object?> plist)
                        throw new AirDropHttpException("/Ask body was not a plist dictionary.");

                    AirDropAskRequest request = AirDropAskRequest.FromPlist(plist);

                    if (await options.ConsentHandler(request, ct))
                    {
                        state = State.Accepted;
                        accepted = request;

                        await RespondPlistAsync(connection, 200, "OK",
                            new AirDropReceiverIdentity(options.ComputerName, options.ModelName).ToPlist(), ct);
                    }
                    else
                    {
                        // Apple signals refusal with 401. It does not mean "authenticate
                        // and retry" here; it means the human said no.
                        state = State.Fresh;
                        accepted = null;
                        await connection.WriteResponseAsync(401, "Unauthorized", new HttpHeaders(), ReadOnlyMemory<byte>.Empty, ct);
                    }

                    break;
                }

                case "/Upload":
                {
                    if (state != State.Accepted || accepted is null)
                    {
                        // Drain so the connection stays framed, then refuse.
                        await using (Stream ignored = connection.OpenBody(head.Headers))
                            await ignored.CopyToAsync(Stream.Null, ct);

                        await connection.WriteResponseAsync(401, "Unauthorized", new HttpHeaders(), ReadOnlyMemory<byte>.Empty, ct);
                        break;
                    }

                    result = await ReceiveUploadAsync(connection, head.Headers, ct);
                    state = State.Completed;

                    await connection.WriteResponseAsync(200, "OK", new HttpHeaders(), ReadOnlyMemory<byte>.Empty, ct);
                    break;
                }

                default:
                    await connection.ReadBodyAsync(head.Headers, options.MaxAskBodyBytes, ct);
                    await connection.WriteResponseAsync(404, "Not Found", new HttpHeaders(), ReadOnlyMemory<byte>.Empty, ct);
                    break;
            }
        }

        return result;
    }

    private async Task<AirDropTransferResult> ReceiveUploadAsync(
        HttpConnection connection,
        HttpHeaders headers,
        CancellationToken ct)
    {
        // How the body's compression is signalled is not fully pinned down. Content-
        // Encoding is honoured when present; otherwise we assume the encoding we
        // advertised, since a well-behaved sender picks from our capability flags.
        string? encoding = headers["Content-Encoding"];

        bool isDvZip = encoding is null
            ? options.Flags.HasFlag(AirDropReceiverFlags.SupportsDvZip)
            : !encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase);

        await using Stream body = connection.OpenBody(headers);

        // Decompression is streamed into a pipe rather than buffered: an AirDrop payload
        // can be gigabytes and must never be held whole in memory.
        var archive = new MemoryStream();
        await AirDropCompression.DecompressAsync(new LimitedStream(body, options.MaxUploadBytes), archive, isDvZip, ct);
        archive.Position = 0;

        Directory.CreateDirectory(options.DownloadDirectory);

        var reader = new CpioReader(archive);
        var written = new List<string>();
        long total = 0;

        while (await reader.ReadNextAsync(ct) is { } entry)
        {
            string destination = ResolveSafePath(options.DownloadDirectory, entry.Name);

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            byte[] content = await reader.ReadContentAsync(ct);
            await File.WriteAllBytesAsync(destination, content, ct);

            written.Add(destination);
            total += content.Length;
        }

        return new AirDropTransferResult(written, total);
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
