using System.Buffers.Binary;
using System.IO.Compression;
using WinDrop.Protocol.Discovery;

namespace WinDrop.Protocol.Compression;

public sealed class DvZipFormatException(string message) : Exception(message);

/// <summary>
/// Apple's chunked compression wrapper for the /Upload body.
///
/// The stream is a sequence of blocks, each a four-byte big-endian length followed by
/// that many bytes of a self-contained zlib (RFC 1950) stream. There is no container
/// header and no explicit terminator: the body ends when the transport says it ends,
/// which for us is the end of the chunked HTTP body.
///
/// Framing it this way rather than as one long deflate stream buys the receiver the
/// ability to start decompressing before the sender has finished producing — each block
/// stands alone, so nothing depends on state carried from earlier in the transfer.
///
/// CONFIRMED against iOS 26.6 (2026-09-13). An iPhone upload began 00 00 00 2D 78 9C: a
/// 45-byte block whose first bytes are a zlib header. A 36 KB JPEG then arrived intact
/// across several blocks. That first block also shows blocks are not a fixed size, since
/// 45 compressed bytes can only be the archive's opening cpio header.
///
/// STORED BLOCKS, CONFIRMED (2026-09-14). Bit 31 of a block header marks a block stored
/// raw, without compression. The reading came from a failed upload whose header was
/// 0x80020000: 0x20000 is exactly 128 KiB, and the file was a JPEG, whose image data
/// deflate cannot shrink. It was confirmed by a 1.79 MB PNG that arrived intact across 12
/// zlib and 3 stored blocks. The first stored block began 54 C3, not a zlib header. A
/// stored block of 0x80001000 (4 KiB) in another upload shows the flag marks a block's
/// type, not a fixed chunk size. "More blocks follow" was ruled out before any of this:
/// an upload's first block was followed by more, yet had bit 31 clear.
///
/// A peer that does not advertise the DVZip capability bit gets plain gzip instead; see
/// <see cref="AirDropCompression"/>.
/// </summary>
public static class DvZip
{
    public const int DefaultBlockSize = 64 * 1024;

    /// <summary>Refuses a block large enough to be an allocation attack from a hostile peer.</summary>
    public const int MaxBlockLength = 16 * 1024 * 1024;

    public static async Task CompressAsync(
        Stream source,
        Stream destination,
        int blockSize = DefaultBlockSize,
        CancellationToken ct = default)
    {
        await using var writer = new DvZipWriteStream(destination, blockSize);
        await source.CopyToAsync(writer, ct);
        await writer.CompleteAsync(ct);
    }

    /// <summary>
    /// Bit 31 of a block header: the block is stored raw rather than zlib-compressed. See
    /// the class remarks for the evidence behind this reading.
    /// </summary>
    internal const uint StoredFlag = 0x8000_0000;

    public static async Task DecompressAsync(
        Stream source,
        Stream destination,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        await using var decoded = new DvZipReadStream(source, log);
        await decoded.CopyToAsync(destination, ct);
        await destination.FlushAsync(ct);
    }

    internal static async Task<int> ReadUpToAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int filled = 0;
        while (filled < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[filled..], ct);
            if (read == 0) break;
            filled += read;
        }

        return filled;
    }
}

/// <summary>
/// Decodes a DVZip stream as it is read, one block at a time, holding neither a whole
/// block nor its inflated output.
///
/// The first decoder read each block into an array and inflated it into a destination the
/// caller supplied, and the receiver supplied a MemoryStream. Inflated output had no limit
/// at all: zlib reaches about a thousand to one on zeros, so a block within the 16 MiB
/// limit could still inflate to gigabytes, and the whole upload sat in RAM before a byte
/// reached disk. Pulled instead of pushed, the caller decides how much it will take, and
/// memory stays at a buffer or two however large the upload is.
///
/// The log lines keep the meaning field sessions have relied on. A stored block is
/// described when it has been read to its end, not when its header arrives, so "block N
/// is stored" still says block N arrived whole. The summary comes at the clean end of the
/// stream, so it appears only if the caller reads that far.
/// </summary>
public sealed class DvZipReadStream(Stream source, Action<string>? log = null) : Stream
{
    private readonly byte[] _header = new byte[4];

    private DvZipBlockStream? _block;
    private ZLibStream? _inflate;
    private uint _blockHeader;
    private int _blocks;
    private int _stored;
    private bool _ended;

    // The first bytes of the first stored block, kept for its log line.
    private readonly byte[] _storedStart = new byte[8];
    private int _storedStartLength;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.IsEmpty) return 0;

        while (!_ended)
        {
            if (_block is null && !await BeginBlockAsync(ct))
            {
                _ended = true;
                log?.Invoke($"dvzip: {_blocks} block(s), {_blocks - _stored} zlib, {_stored} stored");
                return 0;
            }

            int read = _inflate is not null
                ? await _inflate.ReadAsync(buffer, ct)
                : await _block!.ReadAsync(buffer, ct);

            if (read > 0)
            {
                if (_inflate is null) KeepStoredStart(buffer.Span[..read]);
                return read;
            }

            await EndBlockAsync(ct);
        }

        return 0;
    }

    /// <summary>Reads the next block header. False at a clean end between blocks.</summary>
    private async Task<bool> BeginBlockAsync(CancellationToken ct)
    {
        int read = await DvZip.ReadUpToAsync(source, _header, ct);
        if (read == 0) return false;
        if (read < 4) throw new DvZipFormatException($"Truncated block length ({read} of 4 bytes).");

        uint header = BinaryPrimitives.ReadUInt32BigEndian(_header);
        bool isStored = (header & DvZip.StoredFlag) != 0;
        uint length = header & ~DvZip.StoredFlag;

        if (length == 0)
            throw new DvZipFormatException($"Zero-length block (header 0x{header:X8}).");

        // The limit applies to the length, never to the whole header. Read whole, a stored
        // block's header is over two gigabytes, which is exactly how the first large iPhone
        // upload was refused. Nothing is allocated from it any more, but a length this
        // large is still not a block any sender produces.
        if (length > DvZip.MaxBlockLength)
            throw new DvZipFormatException(
                $"Block of {length} bytes (header 0x{header:X8}) exceeds the {DvZip.MaxBlockLength} byte limit.");

        _blocks++;
        _blockHeader = header;
        _block = new DvZipBlockStream(source, length);

        if (isStored)
        {
            _storedStartLength = 0;
            if (++_stored > 1) _storedStartLength = -1; // only the first is described
        }
        else
        {
            _inflate = new ZLibStream(_block, CompressionMode.Decompress, leaveOpen: true);
        }

        return true;
    }

    private async Task EndBlockAsync(CancellationToken ct)
    {
        if (_inflate is not null)
        {
            await _inflate.DisposeAsync();
            _inflate = null;

            // Bytes after the zlib stream's end but inside the block's length are skipped,
            // as the array-based decoder skipped them, so the next header is read from the
            // right place. Reading them also proves the block arrived whole.
            await _block!.CopyToAsync(Stream.Null, ct);
        }
        else if (_storedStartLength >= 0)
        {
            // Only the first is described; a large file can hold dozens. Its leading bytes
            // are what confirmed the stored reading, and they stay in the log as a cheap
            // check that a future iOS has not changed what the flag means.
            log?.Invoke(
                $"dvzip: block {_blocks} is stored (header 0x{_blockHeader:X8}), starts {Convert.ToHexString(_storedStart, 0, _storedStartLength)}");
            _storedStartLength = -1;
        }

        _block = null;
    }

    private void KeepStoredStart(ReadOnlySpan<byte> read)
    {
        if (_storedStartLength < 0 || _storedStartLength == _storedStart.Length) return;

        int take = Math.Min(read.Length, _storedStart.Length - _storedStartLength);
        read[..take].CopyTo(_storedStart.AsSpan(_storedStartLength));
        _storedStartLength += take;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inflate?.Dispose();
            _inflate = null;
        }

        base.Dispose(disposing);
    }

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

/// <summary>
/// Exactly one block's bytes of the underlying stream. Stops the inflater reading into the
/// next block's header, and turns a stream that ends early into a format error rather than
/// a short block.
/// </summary>
internal sealed class DvZipBlockStream(Stream source, long length) : Stream
{
    private readonly long _length = length;
    private long _remaining = length;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_remaining == 0 || buffer.IsEmpty) return 0;

        int read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], ct);
        if (read == 0) throw new DvZipFormatException($"Truncated block: expected {_length} bytes.");

        _remaining -= read;
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Buffers written bytes into fixed-size blocks and emits each as a length-prefixed
/// zlib stream. Does not close the underlying stream: the HTTP body outlives it.
/// </summary>
public sealed class DvZipWriteStream(Stream inner, int blockSize = DvZip.DefaultBlockSize) : Stream
{
    private readonly byte[] _buffer = new byte[blockSize];
    private int _buffered;
    private bool _completed;

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        while (buffer.Length > 0)
        {
            int space = _buffer.Length - _buffered;
            int take = Math.Min(space, buffer.Length);

            buffer[..take].CopyTo(_buffer.AsMemory(_buffered));
            _buffered += take;
            buffer = buffer[take..];

            if (_buffered == _buffer.Length)
                await FlushBlockAsync(ct);
        }
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public async Task CompleteAsync(CancellationToken ct = default)
    {
        if (_completed) return;
        _completed = true;

        // A trailing partial block is emitted; an empty one is not, because a
        // zero-length block is not a legal frame.
        if (_buffered > 0) await FlushBlockAsync(ct);

        await inner.FlushAsync(ct);
    }

    private async Task FlushBlockAsync(CancellationToken ct)
    {
        using var compressed = new MemoryStream();

        // leaveOpen so the MemoryStream survives; disposing ZLibStream is what flushes
        // the final deflate block and writes the Adler-32 trailer, so it must happen
        // before the bytes are read back.
        await using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            await deflate.WriteAsync(_buffer.AsMemory(0, _buffered), ct);
        }

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)compressed.Length);

        await inner.WriteAsync(header, ct);
        compressed.Position = 0;
        await compressed.CopyToAsync(inner, ct);

        _buffered = 0;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        await CompleteAsync();
        await base.DisposeAsync();
    }
}

/// <summary>
/// Picks the encoding for an /Upload body from what the peer advertised in its mDNS TXT
/// record. The capability bit is the whole negotiation — there is no in-band handshake,
/// so sending DVZip to a peer that never claimed to understand it produces a transfer
/// that fails after the user has already accepted it.
/// </summary>
public static class AirDropCompression
{
    public static bool ShouldUseDvZip(AirDropReceiverFlags flags) =>
        flags.HasFlag(AirDropReceiverFlags.SupportsDvZip);

    public static async Task CompressAsync(
        Stream source,
        Stream destination,
        AirDropReceiverFlags peerFlags,
        CancellationToken ct = default)
    {
        if (ShouldUseDvZip(peerFlags))
        {
            await DvZip.CompressAsync(source, destination, DvZip.DefaultBlockSize, ct);
            return;
        }

        await using var gzip = new GZipStream(destination, CompressionLevel.Optimal, leaveOpen: true);
        await source.CopyToAsync(gzip, ct);
    }

    /// <summary>
    /// The decoded upload as a stream the caller pulls from, so it can be unpacked as it
    /// arrives rather than inflated whole first. Does not close <paramref name="source"/>.
    /// </summary>
    public static Stream OpenDecompressor(Stream source, bool isDvZip, Action<string>? log = null) =>
        isDvZip
            ? new DvZipReadStream(source, log)
            : new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);

    public static async Task DecompressAsync(
        Stream source,
        Stream destination,
        bool isDvZip,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        await using Stream decoded = OpenDecompressor(source, isDvZip, log);
        await decoded.CopyToAsync(destination, ct);
    }
}
