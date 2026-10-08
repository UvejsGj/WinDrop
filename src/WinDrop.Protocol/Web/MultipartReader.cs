using System.Text;
using WinDrop.Protocol.Http;

namespace WinDrop.Protocol.Web;

/// <summary>
/// One part of a multipart/form-data body. <see cref="FileName"/> is null for a text
/// field and empty for a file input with nothing chosen.
///
/// <see cref="Body"/> ends where the part ends. Whatever the caller leaves unread is
/// skipped when the next part is asked for.
/// </summary>
internal sealed record FormPart(string? Name, string? FileName, string? ContentType, Stream Body);

/// <summary>
/// A streaming multipart/form-data reader (RFC 7578, framing from RFC 2046), the way a
/// browser's file upload arrives.
///
/// STREAMING, BECAUSE A PART CAN BE A VIDEO. Each part's content is handed on as it is
/// read and never held whole. The one subtlety is the delimiter, CRLF "--" boundary,
/// which can straddle two reads: bytes are passed on only once they cannot be the start
/// of one, so the last few bytes of every buffer wait for the next read to settle them.
///
/// Every header line and the number of headers are bounded. The peer is a phone that
/// knows the link, which is not the same as trusted, and nothing it sends is allowed to
/// grow without limit before the person at the PC has agreed to anything.
/// </summary>
internal sealed class MultipartReader
{
    private const int MaxHeaderLine = 8 * 1024;
    private const int MaxHeadersPerPart = 16;
    private const int MaxBoundaryLength = 70;

    private readonly Stream _source;
    private readonly byte[] _delimiter;
    private readonly byte[] _buffer;
    private int _start;
    private int _end;
    private bool _sourceDone;

    // The preamble before the first boundary is read as if it were a part, and dropped.
    private bool _inPart = true;
    private bool _finished;

    // Counts parts, so a stream handed out for one part reads nothing of the next.
    private int _part;

    public MultipartReader(Stream source, string boundary)
    {
        _source = source;
        _delimiter = Encoding.ASCII.GetBytes("\r\n--" + boundary);
        _buffer = new byte[64 * 1024 + _delimiter.Length];

        // The body opens with "--boundary", without the CRLF that every later delimiter
        // carries. Starting the buffer with one makes the first look like the rest.
        _buffer[0] = (byte)'\r';
        _buffer[1] = (byte)'\n';
        _end = 2;
    }

    /// <summary>
    /// The boundary parameter of a multipart/form-data content type, or null when the type
    /// is something else or the boundary is not one RFC 2046 allows.
    /// </summary>
    public static string? BoundaryFrom(string? contentType)
    {
        if (contentType is null) return null;

        string[] pieces = contentType.Split(';');
        if (!string.Equals(pieces[0].Trim(), "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (string piece in pieces.Skip(1))
        {
            int equals = piece.IndexOf('=');
            if (equals < 0) continue;

            if (!string.Equals(piece[..equals].Trim(), "boundary", StringComparison.OrdinalIgnoreCase))
                continue;

            string boundary = piece[(equals + 1)..].Trim();
            if (boundary.Length >= 2 && boundary[0] == '"' && boundary[^1] == '"')
                boundary = boundary[1..^1];

            // 1 to 70 characters, none of them control characters, and not ending in a space.
            if (boundary.Length is 0 or > MaxBoundaryLength) return null;
            if (boundary.Any(c => c < 0x20 || c > 0x7E) || boundary.EndsWith(' ')) return null;

            return boundary;
        }

        return null;
    }

    /// <summary>The next part, or null after the closing delimiter.</summary>
    public async Task<FormPart?> ReadNextPartAsync(CancellationToken ct = default)
    {
        if (_finished) return null;

        // Whatever the caller did not read of the last part, or the preamble.
        while (_inPart)
        {
            byte[] skip = new byte[16 * 1024];
            while (await ReadPartAsync(skip, ct) > 0) { }
        }

        // After a delimiter: "--" closes the body, otherwise optional spaces and a CRLF.
        if (!await EnsureAsync(2, ct))
            throw new AirDropHttpException("The form ended right after a boundary.");

        if (_buffer[_start] == '-' && _buffer[_start + 1] == '-')
        {
            _start += 2;
            _finished = true;
            return null;
        }

        string rest = await ReadHeaderLineAsync(ct);
        if (rest.Trim(' ', '\t').Length != 0)
            throw new AirDropHttpException("A form boundary was followed by something other than a line end.");

        string? name = null, fileName = null, contentType = null;
        int headers = 0;

        while (true)
        {
            string line = await ReadHeaderLineAsync(ct);
            if (line.Length == 0) break;

            if (++headers > MaxHeadersPerPart)
                throw new AirDropHttpException($"A form part has more than {MaxHeadersPerPart} headers.");

            int colon = line.IndexOf(':');
            if (colon <= 0) throw new AirDropHttpException("A form part has a malformed header.");

            string header = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();

            if (string.Equals(header, "Content-Disposition", StringComparison.OrdinalIgnoreCase))
                (name, fileName) = ParseDisposition(value);
            else if (string.Equals(header, "Content-Type", StringComparison.OrdinalIgnoreCase))
                contentType = value;
        }

        _inPart = true;
        _part++;
        return new FormPart(name, fileName, contentType, new PartStream(this, _part));
    }

    /// <summary>
    /// The name and filename parameters of a form-data Content-Disposition. filename* (RFC
    /// 8187, UTF-8 percent-encoded) wins over filename when both are present, as RFC 6266
    /// says. Browsers send neither escaped: they percent-encode a quote or a line break in
    /// the name instead, so a name arrives with %22 in it rather than an escaped quote. That
    /// is left alone; decoding it could turn a harmless name into one with a quote in it.
    /// </summary>
    internal static (string? Name, string? FileName) ParseDisposition(string value)
    {
        List<string> pieces = SplitOutsideQuotes(value);

        if (!string.Equals(pieces[0].Trim(), "form-data", StringComparison.OrdinalIgnoreCase))
            throw new AirDropHttpException("A form part is not form-data.");

        string? name = null, fileName = null, extendedFileName = null;

        foreach (string piece in pieces.Skip(1))
        {
            int equals = piece.IndexOf('=');
            if (equals < 0) continue;

            string key = piece[..equals].Trim();
            string raw = piece[(equals + 1)..].Trim();

            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
                name = Unquote(raw);
            else if (string.Equals(key, "filename", StringComparison.OrdinalIgnoreCase))
                fileName = Unquote(raw);
            else if (string.Equals(key, "filename*", StringComparison.OrdinalIgnoreCase))
                extendedFileName = DecodeExtended(raw);
        }

        return (name, extendedFileName ?? fileName);
    }

    private static List<string> SplitOutsideQuotes(string value)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];

            if (quoted && c == '\\' && i + 1 < value.Length)
            {
                current.Append(c).Append(value[++i]);
                continue;
            }

            if (c == '"') quoted = !quoted;

            if (c == ';' && !quoted)
            {
                pieces.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        pieces.Add(current.ToString());
        return pieces;
    }

    private static string Unquote(string raw)
    {
        if (raw.Length < 2 || raw[0] != '"' || raw[^1] != '"') return raw;

        var result = new StringBuilder(raw.Length);

        for (int i = 1; i < raw.Length - 1; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length - 1) i++;
            result.Append(raw[i]);
        }

        return result.ToString();
    }

    /// <summary>charset'language'percent-encoded, UTF-8 only. Anything else counts as absent.</summary>
    private static string? DecodeExtended(string raw)
    {
        int first = raw.IndexOf('\'');
        int second = first < 0 ? -1 : raw.IndexOf('\'', first + 1);
        if (second < 0) return null;

        if (!string.Equals(raw[..first], "UTF-8", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            return Uri.UnescapeDataString(raw[(second + 1)..]);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    // ---- part content --------------------------------------------------------

    /// <summary>Reads the current part's content; 0 once its closing delimiter is reached.</summary>
    internal ValueTask<int> ReadPartAsync(int part, Memory<byte> destination, CancellationToken ct) =>
        part == _part ? ReadPartAsync(destination, ct) : ValueTask.FromResult(0);

    private async ValueTask<int> ReadPartAsync(Memory<byte> destination, CancellationToken ct)
    {
        if (!_inPart || destination.Length == 0) return 0;

        while (true)
        {
            int available = _end - _start;
            int index = _buffer.AsSpan(_start, available).IndexOf(_delimiter);

            if (index == 0)
            {
                _start += _delimiter.Length;
                _inPart = false;
                return 0;
            }

            // Content runs up to the delimiter, or, with none in sight, up to the last few
            // bytes, which could be the start of one that the next read completes.
            int safe = index > 0 ? index : available - (_delimiter.Length - 1);

            if (safe > 0)
            {
                int count = Math.Min(safe, destination.Length);
                _buffer.AsSpan(_start, count).CopyTo(destination.Span);
                _start += count;
                return count;
            }

            if (!await FillAsync(ct))
                throw new AirDropHttpException("The form ended inside a part.");
        }
    }

    private async Task<string> ReadHeaderLineAsync(CancellationToken ct)
    {
        while (true)
        {
            int newline = _buffer.AsSpan(_start, _end - _start).IndexOf((byte)'\n');

            if (newline >= 0)
            {
                int length = newline;
                if (length > 0 && _buffer[_start + length - 1] == '\r') length--;

                if (length > MaxHeaderLine)
                    throw new AirDropHttpException("A form part header is too long.");

                // UTF-8: browsers send a file name's own characters here, not an encoding of them.
                string line = Encoding.UTF8.GetString(_buffer, _start, length);
                _start += newline + 1;
                return line;
            }

            if (_end - _start > MaxHeaderLine)
                throw new AirDropHttpException("A form part header is too long.");

            if (!await FillAsync(ct))
                throw new AirDropHttpException("The form ended inside a part's headers.");
        }
    }

    private async Task<bool> EnsureAsync(int count, CancellationToken ct)
    {
        while (_end - _start < count)
        {
            if (!await FillAsync(ct)) return false;
        }

        return true;
    }

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        // Cannot happen while reading content, which always hands bytes on before the buffer
        // fills, nor a header line, which is capped well below the buffer. A guard anyway.
        if (_end == _buffer.Length)
            throw new AirDropHttpException("The form reader's buffer filled without progress.");

        if (_sourceDone) return false;

        int read = await _source.ReadAsync(_buffer.AsMemory(_end), ct);
        if (read == 0)
        {
            _sourceDone = true;
            return false;
        }

        _end += read;
        return true;
    }

    private sealed class PartStream(MultipartReader reader, int part) : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            reader.ReadPartAsync(part, buffer, ct);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            reader.ReadPartAsync(part, buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            reader.ReadPartAsync(part, buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

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
}
