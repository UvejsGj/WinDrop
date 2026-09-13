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
/// STORED BLOCKS, which is a reading, not a confirmation. A larger iPhone upload carried
/// the header 0x80020000. Without bit 31 that is 0x20000, exactly 128 KiB, and the file
/// was a JPEG, whose image data deflate cannot shrink. So bit 31 is read as marking a
/// block stored raw, because compressing it would have made it bigger. The obvious
/// alternative, "more blocks follow", is refused by the upload above: its first block
/// was followed by more, yet had bit 31 clear. The first stored block's leading bytes are
/// logged, so the next real transfer either bears this out or shows what the block holds.
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
        var headerBytes = new byte[4];
        int blocks = 0;
        int stored = 0;

        while (true)
        {
            int read = await ReadUpToAsync(source, headerBytes, ct);
            if (read == 0) break; // clean end of stream between blocks
            if (read < 4) throw new DvZipFormatException($"Truncated block length ({read} of 4 bytes).");

            uint header = BinaryPrimitives.ReadUInt32BigEndian(headerBytes);
            bool isStored = (header & StoredFlag) != 0;
            uint length = header & ~StoredFlag;

            if (length == 0)
                throw new DvZipFormatException($"Zero-length block (header 0x{header:X8}).");

            // The limit applies to the length, never to the whole header. Read whole, a
            // stored block's header is over two gigabytes, which is exactly how the first
            // large iPhone upload was refused.
            if (length > MaxBlockLength)
                throw new DvZipFormatException(
                    $"Block of {length} bytes (header 0x{header:X8}) exceeds the {MaxBlockLength} byte limit.");

            var block = new byte[length];
            if (await ReadUpToAsync(source, block, ct) != block.Length)
                throw new DvZipFormatException($"Truncated block: expected {length} bytes.");

            blocks++;

            if (isStored)
            {
                // Only the first is described; a large file can hold dozens. If the reading
                // is wrong, these bytes are where it shows: raw file data supports it, and
                // a zlib header (78 xx) here would refute it.
                if (++stored == 1)
                {
                    log?.Invoke(
                        $"dvzip: block {blocks} is stored (header 0x{header:X8}), starts {Convert.ToHexString(block, 0, Math.Min(8, block.Length))}");
                }

                await destination.WriteAsync(block, ct);
                continue;
            }

            using var compressed = new MemoryStream(block, writable: false);
            await using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
            await inflate.CopyToAsync(destination, ct);
        }

        log?.Invoke($"dvzip: {blocks} block(s), {blocks - stored} zlib, {stored} stored");
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

    public static async Task DecompressAsync(
        Stream source,
        Stream destination,
        bool isDvZip,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        if (isDvZip)
        {
            await DvZip.DecompressAsync(source, destination, log, ct);
            return;
        }

        await using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
        await gzip.CopyToAsync(destination, ct);
    }
}
