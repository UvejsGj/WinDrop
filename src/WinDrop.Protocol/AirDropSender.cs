using WinDrop.Protocol.Archive;
using WinDrop.Protocol.Compression;
using WinDrop.Protocol.Discovery;
using WinDrop.Protocol.Http;
using WinDrop.Protocol.Plist;

namespace WinDrop.Protocol;

/// <summary>A local file paired with the name it will carry inside the archive.</summary>
public sealed record AirDropOutgoingFile(string LocalPath, string FileName, string? FileType = null)
{
    public static AirDropOutgoingFile FromPath(string path) =>
        new(path, Path.GetFileName(path));

    public string BomPath => $"./{FileName}";

    public AirDropFileEntry ToEntry() =>
        new(FileName, FileType ?? AirDropFileEntry.DefaultFileType, BomPath);
}

/// <summary>
/// The sending half, driving one connection through Discover, Ask and Upload.
///
/// The whole session is deliberately one object over one stream. /Ask and /Upload must
/// reach the receiver on the same connection, because that is how the receiver ties the
/// user's consent to the bytes that follow — so the connection is a member here, not
/// something fetched per request from a pool.
/// </summary>
public sealed class AirDropSenderSession : IAsyncDisposable
{
    private readonly HttpConnection _connection;
    private readonly AirDropReceiverFlags _peerFlags;
    private bool _accepted;

    public AirDropSenderSession(Stream tlsStream, AirDropReceiverFlags peerFlags, bool ownsStream = false)
    {
        _connection = new HttpConnection(tlsStream, ownsStream);
        _peerFlags = peerFlags;
    }

    public bool UsesDvZip => AirDropCompression.ShouldUseDvZip(_peerFlags);

    /// <summary>
    /// Optional identity exchange. In Contacts-Only mode this is where the two sides
    /// compare contact hashes; in Everyone mode it yields nothing but the receiver's
    /// display name, which is still worth having so the user can confirm the target.
    /// </summary>
    public async Task<AirDropReceiverIdentity?> DiscoverAsync(CancellationToken ct = default)
    {
        var body = BinaryPlistWriter.Write(new Dictionary<string, object?>());

        await PostPlistAsync("/Discover", body, ct);

        HttpResponseHead response = await _connection.ReadResponseHeadAsync(ct);
        byte[] payload = await _connection.ReadBodyAsync(response.Headers, 1024 * 1024, ct);

        if (!response.IsSuccess) return null;

        return BinaryPlistReader.Parse(payload) is IReadOnlyDictionary<string, object?> plist
            ? AirDropReceiverIdentity.FromPlist(plist)
            : null;
    }

    /// <summary>Asks for consent. False means the user declined; the session cannot upload.</summary>
    public async Task<bool> AskAsync(AirDropAskRequest request, CancellationToken ct = default)
    {
        await PostPlistAsync("/Ask", BinaryPlistWriter.Write(request.ToPlist()), ct);

        HttpResponseHead response = await _connection.ReadResponseHeadAsync(ct);
        await _connection.ReadBodyAsync(response.Headers, 1024 * 1024, ct);

        _accepted = response.IsSuccess;
        return _accepted;
    }

    /// <summary>
    /// Streams the files as cpio, compressed, in a chunked body. Nothing is buffered
    /// whole: the archive is produced straight into the compressor, which writes into
    /// the HTTP chunk writer as it goes.
    /// </summary>
    public async Task UploadAsync(IReadOnlyList<AirDropOutgoingFile> files, CancellationToken ct = default)
    {
        if (!_accepted)
            throw new InvalidOperationException("Upload attempted before /Ask was accepted.");

        var headers = new HttpHeaders();
        headers.Set("Content-Type", "application/x-cpio");

        // Say which encoding was used rather than leaving the receiver to infer it from
        // its own advertised flags.
        headers.Set("Content-Encoding", UsesDvZip ? "dvzip" : "gzip");

        await _connection.WriteStreamingRequestAsync("POST", "/Upload", headers,
            async (bodyStream, token) =>
            {
                await using Stream compressor = UsesDvZip
                    ? new DvZipWriteStream(bodyStream)
                    : new System.IO.Compression.GZipStream(
                        bodyStream, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true);

                var archive = new CpioWriter(compressor);

                foreach (AirDropOutgoingFile file in files)
                {
                    var info = new FileInfo(file.LocalPath);
                    await using FileStream source = File.OpenRead(file.LocalPath);

                    await archive.WriteFileAsync(
                        file.BomPath, source, info.Length,
                        modified: info.LastWriteTimeUtc, ct: token);
                }

                await archive.CompleteAsync(token);

                if (compressor is DvZipWriteStream dvzip)
                    await dvzip.CompleteAsync(token);
            }, ct);

        HttpResponseHead response = await _connection.ReadResponseHeadAsync(ct);
        await _connection.ReadBodyAsync(response.Headers, 64 * 1024, ct);

        if (!response.IsSuccess)
            throw new AirDropHttpException($"Upload rejected: {response.StatusCode} {response.ReasonPhrase}");
    }

    private Task PostPlistAsync(string target, byte[] body, CancellationToken ct)
    {
        var headers = new HttpHeaders();
        headers.Set("Content-Type", "application/octet-stream");

        return _connection.WriteRequestAsync("POST", target, headers, body, ct);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
