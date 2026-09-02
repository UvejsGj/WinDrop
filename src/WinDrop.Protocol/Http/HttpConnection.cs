using System.Buffers;
using System.Globalization;
using System.Text;

namespace WinDrop.Protocol.Http;

/// <summary>
/// A minimal HTTP/1.1 client and server over a single duplex stream.
///
/// WHY NOT HttpClient. AirDrop requires /Ask and /Upload to travel over the same TLS
/// connection — the receiver ties the user's consent decision to the connection it was
/// granted on. HttpClient offers no guarantee about which pooled connection a request
/// lands on, so the one property the protocol depends on is the one it will not
/// promise. Owning the connection is the only way to be sure.
///
/// The reader buffers, so it must own the boundary between head and body: after the
/// blank line it will usually have already pulled body bytes into its buffer. Body
/// streams therefore read through this class rather than from the socket directly.
/// </summary>
public sealed class HttpConnection(Stream stream, bool ownsStream = true) : IAsyncDisposable
{
    private const int MaxLineLength = 8 * 1024;
    private const int MaxHeaderCount = 100;

    private readonly Stream _stream = stream;
    private byte[] _buffer = new byte[8192];
    private int _start;
    private int _end;

    public Stream Stream => _stream;

    // ---- reading -----------------------------------------------------------

    /// <summary>Reads a request head, or null when the peer closed the connection cleanly.</summary>
    public async Task<HttpRequestHead?> ReadRequestHeadAsync(CancellationToken ct = default)
    {
        string? line = await ReadLineAsync(ct);
        if (line is null) return null;

        // Tolerate a leading blank line, which RFC 9112 allows a server to ignore.
        if (line.Length == 0)
        {
            line = await ReadLineAsync(ct);
            if (line is null) return null;
        }

        string[] parts = line.Split(' ', 3);
        if (parts.Length != 3)
            throw new AirDropHttpException($"Malformed request line '{Truncate(line)}'.");

        if (!parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            throw new AirDropHttpException($"Unsupported HTTP version '{parts[2]}'.");

        return new HttpRequestHead(parts[0], parts[1], await ReadHeadersAsync(ct));
    }

    public async Task<HttpResponseHead> ReadResponseHeadAsync(CancellationToken ct = default)
    {
        string line = await ReadLineAsync(ct)
            ?? throw new AirDropHttpException("Connection closed before a response was received.");

        string[] parts = line.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal))
            throw new AirDropHttpException($"Malformed status line '{Truncate(line)}'.");

        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int status))
            throw new AirDropHttpException($"Malformed status code '{parts[1]}'.");

        return new HttpResponseHead(status, parts.Length > 2 ? parts[2] : "", await ReadHeadersAsync(ct));
    }

    private async Task<HttpHeaders> ReadHeadersAsync(CancellationToken ct)
    {
        var headers = new HttpHeaders();

        while (true)
        {
            string line = await ReadLineAsync(ct)
                ?? throw new AirDropHttpException("Connection closed inside the header block.");

            if (line.Length == 0) break;

            if (headers.Count >= MaxHeaderCount)
                throw new AirDropHttpException($"More than {MaxHeaderCount} headers.");

            // A leading space would be an obs-fold continuation. Refusing it is both
            // simpler and safer than trying to reassemble it.
            if (line[0] is ' ' or '\t')
                throw new AirDropHttpException("Obsolete line folding in headers.");

            int colon = line.IndexOf(':');
            if (colon <= 0)
                throw new AirDropHttpException($"Malformed header '{Truncate(line)}'.");

            headers.Add(line[..colon].TrimEnd(), line[(colon + 1)..].Trim());
        }

        return headers;
    }

    /// <summary>
    /// Opens the body of a message whose head has just been read. Returns a stream that
    /// ends exactly where the body ends, so the connection is left positioned at the
    /// start of the next message and can be reused.
    /// </summary>
    public Stream OpenBody(HttpHeaders headers)
    {
        if (headers.IsChunked)
            return new ChunkedReadStream(this);

        long length = headers.ContentLength ?? 0;
        if (length < 0)
            throw new AirDropHttpException("Negative Content-Length.");

        return new FixedLengthReadStream(this, length);
    }

    public async Task<byte[]> ReadBodyAsync(HttpHeaders headers, int maxBytes, CancellationToken ct = default)
    {
        await using Stream body = OpenBody(headers);

        var buffer = new MemoryStream();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            while (true)
            {
                int read = await body.ReadAsync(chunk, ct);
                if (read == 0) break;

                if (buffer.Length + read > maxBytes)
                    throw new AirDropHttpException($"Body exceeds the {maxBytes} byte limit.");

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }

    // ---- writing -----------------------------------------------------------

    public async Task WriteRequestAsync(
        string method,
        string target,
        HttpHeaders headers,
        ReadOnlyMemory<byte> body,
        CancellationToken ct = default)
    {
        headers.Set("Content-Length", body.Length.ToString(CultureInfo.InvariantCulture));

        var head = new StringBuilder();
        head.Append(method).Append(' ').Append(target).Append(" HTTP/1.1\r\n");
        AppendHeaders(head, headers);

        await _stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        if (body.Length > 0) await _stream.WriteAsync(body, ct);
        await _stream.FlushAsync(ct);
    }

    /// <summary>
    /// Streams a request body of unknown length using chunked transfer-encoding. Used
    /// for /Upload, where the archive is generated on the fly and buffering it whole to
    /// learn its length would defeat the point of streaming.
    /// </summary>
    public async Task WriteStreamingRequestAsync(
        string method,
        string target,
        HttpHeaders headers,
        Func<Stream, CancellationToken, Task> writeBody,
        CancellationToken ct = default)
    {
        headers.Set("Transfer-Encoding", "chunked");

        var head = new StringBuilder();
        head.Append(method).Append(' ').Append(target).Append(" HTTP/1.1\r\n");
        AppendHeaders(head, headers);

        await _stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);

        await using (var chunked = new ChunkedWriteStream(_stream))
        {
            await writeBody(chunked, ct);
            await chunked.CompleteAsync(ct);
        }

        await _stream.FlushAsync(ct);
    }

    public async Task WriteResponseAsync(
        int status,
        string reason,
        HttpHeaders headers,
        ReadOnlyMemory<byte> body,
        CancellationToken ct = default)
    {
        headers.Set("Content-Length", body.Length.ToString(CultureInfo.InvariantCulture));

        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture))
            .Append(' ').Append(reason).Append("\r\n");
        AppendHeaders(head, headers);

        await _stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        if (body.Length > 0) await _stream.WriteAsync(body, ct);
        await _stream.FlushAsync(ct);
    }

    private static void AppendHeaders(StringBuilder head, HttpHeaders headers)
    {
        foreach (var (name, value) in headers)
        {
            // A header value containing CR or LF would let a peer inject an entire
            // extra message into the stream. Never emit one.
            if (value.AsSpan().IndexOfAny('\r', '\n') >= 0 || name.AsSpan().IndexOfAny('\r', '\n') >= 0)
                throw new AirDropHttpException($"Header '{name}' contains a line break.");

            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        head.Append("\r\n");
    }

    // ---- buffered reading primitives ---------------------------------------

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        int scanned = 0;

        while (true)
        {
            int newline = Array.IndexOf(_buffer, (byte)'\n', _start + scanned, _end - _start - scanned);

            if (newline >= 0)
            {
                int lineEnd = newline;
                if (lineEnd > _start && _buffer[lineEnd - 1] == (byte)'\r') lineEnd--;

                string line = Encoding.ASCII.GetString(_buffer, _start, lineEnd - _start);
                _start = newline + 1;
                return line;
            }

            scanned = _end - _start;

            if (scanned > MaxLineLength)
                throw new AirDropHttpException($"Line longer than {MaxLineLength} bytes.");

            if (!await FillAsync(ct))
                return scanned == 0 ? null : throw new AirDropHttpException("Connection closed mid-line.");
        }
    }

    /// <summary>Pulls more bytes in, compacting or growing the buffer as needed.</summary>
    private async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
            Array.Resize(ref _buffer, _buffer.Length * 2);

        int read = await _stream.ReadAsync(_buffer.AsMemory(_end), ct);
        if (read == 0) return false;

        _end += read;
        return true;
    }

    /// <summary>Reads body bytes, draining the head-parsing buffer before the socket.</summary>
    internal async ValueTask<int> ReadRawAsync(Memory<byte> destination, CancellationToken ct)
    {
        if (_start < _end)
        {
            int available = Math.Min(destination.Length, _end - _start);
            _buffer.AsMemory(_start, available).CopyTo(destination);
            _start += available;
            return available;
        }

        return await _stream.ReadAsync(destination, ct);
    }

    internal async ValueTask ReadExactlyRawAsync(Memory<byte> destination, CancellationToken ct)
    {
        int filled = 0;
        while (filled < destination.Length)
        {
            int read = await ReadRawAsync(destination[filled..], ct);
            if (read == 0) throw new AirDropHttpException("Connection closed mid-body.");
            filled += read;
        }
    }

    internal Task<string?> ReadRawLineAsync(CancellationToken ct) => ReadLineAsync(ct);

    private static string Truncate(string value) => value.Length <= 80 ? value : value[..80] + "...";

    public async ValueTask DisposeAsync()
    {
        if (ownsStream) await _stream.DisposeAsync();
    }
}

/// <summary>Body delimited by Content-Length. Ends exactly at the declared length.</summary>
internal sealed class FixedLengthReadStream(HttpConnection connection, long length) : Stream
{
    private long _remaining = length;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_remaining == 0) return 0;

        int wanted = (int)Math.Min(buffer.Length, _remaining);
        int read = await connection.ReadRawAsync(buffer[..wanted], ct);

        if (read == 0)
            throw new AirDropHttpException($"Connection closed with {_remaining} body bytes outstanding.");

        _remaining -= read;
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => length - _remaining; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Chunked transfer-encoding reader. Each chunk is a hex length, CRLF, the bytes, CRLF,
/// terminated by a zero-length chunk. The trailing CRLF after every chunk is not
/// optional, and a reader that skips it silently desynchronises on the next message.
/// </summary>
internal sealed class ChunkedReadStream(HttpConnection connection) : Stream
{
    private long _chunkRemaining;
    private bool _finished;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_finished) return 0;

        if (_chunkRemaining == 0)
        {
            string line = await connection.ReadRawLineAsync(ct)
                ?? throw new AirDropHttpException("Connection closed before a chunk header.");

            // Chunk extensions after a ';' are legal and ignorable.
            int semicolon = line.IndexOf(';');
            string sizeText = (semicolon >= 0 ? line[..semicolon] : line).Trim();

            if (!long.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long size) || size < 0)
                throw new AirDropHttpException($"Malformed chunk size '{sizeText}'.");

            if (size == 0)
            {
                // Consume trailers up to the terminating blank line.
                while (true)
                {
                    string? trailer = await connection.ReadRawLineAsync(ct);
                    if (trailer is null || trailer.Length == 0) break;
                }

                _finished = true;
                return 0;
            }

            _chunkRemaining = size;
        }

        int wanted = (int)Math.Min(buffer.Length, _chunkRemaining);
        int read = await connection.ReadRawAsync(buffer[..wanted], ct);
        if (read == 0) throw new AirDropHttpException("Connection closed mid-chunk.");

        _chunkRemaining -= read;

        if (_chunkRemaining == 0)
        {
            string? terminator = await connection.ReadRawLineAsync(ct);
            if (terminator is not "")
                throw new AirDropHttpException("Chunk was not followed by CRLF.");
        }

        return read;
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

/// <summary>Chunked writer. Does not close the underlying stream — the connection outlives the body.</summary>
internal sealed class ChunkedWriteStream(Stream inner) : Stream
{
    private bool _completed;

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        // A zero-length chunk would be read as the terminator, ending the body early.
        if (buffer.Length == 0) return;

        await inner.WriteAsync(
            Encoding.ASCII.GetBytes($"{buffer.Length:X}\r\n"), ct);
        await inner.WriteAsync(buffer, ct);
        await inner.WriteAsync("\r\n"u8.ToArray(), ct);
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public async Task CompleteAsync(CancellationToken ct = default)
    {
        if (_completed) return;
        _completed = true;

        await inner.WriteAsync("0\r\n\r\n"u8.ToArray(), ct);
        await inner.FlushAsync(ct);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
