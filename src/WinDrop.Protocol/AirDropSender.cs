using WinDrop.Protocol.Archive;
using WinDrop.Protocol.Compression;
using WinDrop.Protocol.Discovery;
using WinDrop.Protocol.Http;
using WinDrop.Protocol.Plist;

namespace WinDrop.Protocol;

/// <summary>
/// One entry as it will appear inside the cpio archive. <see cref="LocalPath"/> is null
/// for directories, which carry no content of their own.
/// </summary>
public sealed record ArchiveMember(string? LocalPath, string BomPath, bool IsDirectory, long Length);

/// <summary>
/// A selected item paired with the name it will carry inside the archive.
///
/// A selection is one item as the user sees it — a file, or a folder — and that is what
/// the receiver is asked to consent to. The archive underneath may hold many more
/// entries: a folder expands to itself plus everything beneath it, with paths relative
/// to the folder so the tree survives the transfer.
/// </summary>
public sealed record AirDropOutgoingFile(string LocalPath, string FileName, string? FileType = null)
{
    public static AirDropOutgoingFile FromPath(string path)
    {
        // A trailing separator would make GetFileName return empty for a directory.
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return new AirDropOutgoingFile(path, Path.GetFileName(trimmed));
    }

    public bool IsDirectory => Directory.Exists(LocalPath);

    public string BomPath => $"./{FileName}";

    // The type is taken from FileName rather than from LocalPath: the two carry the same
    // extension, but FileName is the one the receiver is shown, and the entry should not
    // be able to describe a file by one name and type it by another.
    public AirDropFileEntry ToEntry() => new(
        FileName,
        FileType ?? (IsDirectory ? UniformTypeIdentifiers.Folder : UniformTypeIdentifiers.ForFileName(FileName)),
        BomPath,
        IsDirectory);

    public long TotalBytes => EnumerateMembers().Sum(m => m.Length);

    /// <summary>
    /// Flattens this selection into archive entries, depth first and parents before
    /// children — a receiver creating directories as it goes needs them in that order.
    /// </summary>
    public IEnumerable<ArchiveMember> EnumerateMembers()
    {
        if (!IsDirectory)
        {
            yield return new ArchiveMember(LocalPath, BomPath, false, new FileInfo(LocalPath).Length);
            yield break;
        }

        yield return new ArchiveMember(null, BomPath, true, 0);

        foreach (ArchiveMember member in Walk(LocalPath, BomPath))
            yield return member;
    }

    private static IEnumerable<ArchiveMember> Walk(string directory, string bomPrefix)
    {
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            var info = new DirectoryInfo(child);

            // Reparse points are junctions and symlinks. Following them can loop forever,
            // and can also reach outside the tree the user actually chose to send.
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

            string bom = $"{bomPrefix}/{info.Name}";
            yield return new ArchiveMember(null, bom, true, 0);

            foreach (ArchiveMember member in Walk(child, bom))
                yield return member;
        }

        foreach (string child in Directory.EnumerateFiles(directory))
        {
            var info = new FileInfo(child);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

            yield return new ArchiveMember(child, $"{bomPrefix}/{info.Name}", false, info.Length);
        }
    }
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
    /// Streams the selection as cpio, compressed, in a chunked body. Nothing is buffered
    /// whole: the archive is produced straight into the compressor, which writes into
    /// the HTTP chunk writer as it goes.
    /// </summary>
    public async Task UploadAsync(
        IReadOnlyList<AirDropOutgoingFile> files,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        if (!_accepted)
            throw new InvalidOperationException("Upload attempted before /Ask was accepted.");

        var headers = new HttpHeaders();
        headers.Set("Content-Type", "application/x-cpio");

        // Say which encoding was used rather than leaving the receiver to infer it. Note
        // a real peer may send nothing here at all — opendrop does not — so a receiver
        // must not depend on this header being present.
        headers.Set("Content-Encoding", UsesDvZip ? "dvzip" : "gzip");

        await _connection.WriteStreamingRequestAsync("POST", "/Upload", headers,
            async (bodyStream, token) =>
            {
                await using Stream compressor = UsesDvZip
                    ? new DvZipWriteStream(bodyStream)
                    : new System.IO.Compression.GZipStream(
                        bodyStream, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true);

                var archive = new CpioWriter(compressor);
                long sent = 0;

                foreach (AirDropOutgoingFile file in files)
                {
                    foreach (ArchiveMember member in file.EnumerateMembers())
                    {
                        if (member.IsDirectory)
                        {
                            await archive.WriteDirectoryAsync(member.BomPath, token);
                            continue;
                        }

                        var info = new FileInfo(member.LocalPath!);
                        await using FileStream source = File.OpenRead(member.LocalPath!);

                        // Progress is measured on bytes read out of the source, not bytes
                        // written to the socket: the compressor buffers, so socket writes
                        // arrive in lumps that would make a progress bar stutter.
                        await using Stream tracked = progress is null
                            ? source
                            : new CountingStream(source, n => progress.Report(Interlocked.Add(ref sent, n)));

                        await archive.WriteFileAsync(
                            member.BomPath, tracked, info.Length,
                            modified: info.LastWriteTimeUtc, ct: token);
                    }
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

/// <summary>Counts bytes as they are read, so a caller can show progress.</summary>
internal sealed class CountingStream(Stream inner, Action<int> onRead) : Stream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int read = await inner.ReadAsync(buffer, ct);
        if (read > 0) onRead(read);
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
