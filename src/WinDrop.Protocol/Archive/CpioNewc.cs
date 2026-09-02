using System.Globalization;
using System.Text;

namespace WinDrop.Protocol.Archive;

public sealed class CpioFormatException(string message) : Exception(message);

/// <summary>
/// One archive member. <see cref="Name"/> carries the path exactly as it appears in the
/// archive, which for AirDrop is the same relative form used in the FileBomPath field of
/// the /Ask body ("./photo.jpg") — the two have to agree or the receiver cannot match a
/// member to the metadata it consented to.
/// </summary>
public sealed record CpioEntry(string Name, int Mode, long Size, long ModifiedUnixTime)
{
    public const int RegularFileMode = 0b1000_000_110_100_100; // 0o100644
    public const int DirectoryMode = 0b0100_000_111_101_101;   // 0o040755

    public bool IsDirectory => (Mode & 0xF000) == 0x4000;
    public bool IsRegularFile => (Mode & 0xF000) == 0x8000;
}

/// <summary>
/// The "newc" (SVR4, no checksum) cpio format that AirDrop uses to carry files.
///
/// The header is 110 bytes of ASCII hexadecimal — thirteen fields of eight characters
/// after a six-character magic — which means the whole thing is human-readable in a hex
/// dump, and a wrong field shows up as garbage rather than as a subtly wrong number.
///
/// Two padding rules, and both are easy to get wrong because they are counted from
/// different origins:
///   * the header plus the NUL-terminated name is padded to a 4-byte boundary
///   * the file data is padded, independently, to a 4-byte boundary
/// Miss either and every subsequent header lands off-alignment, so the archive appears
/// to contain one valid file followed by rubbish.
///
/// The archive ends with a member literally named "TRAILER!!!" of zero length.
/// </summary>
public static class CpioNewc
{
    public const string TrailerName = "TRAILER!!!";
    internal const string Magic = "070701";
    internal const int HeaderLength = 110;

    internal static int PadTo4(long value) => (int)((4 - (value % 4)) % 4);
}

public sealed class CpioWriter(Stream output) : IAsyncDisposable
{
    private static readonly byte[] Padding = new byte[4];

    private long _position;
    private long _nextInode = 1;
    private bool _completed;

    public async Task WriteFileAsync(
        string name,
        ReadOnlyMemory<byte> content,
        int mode = CpioEntry.RegularFileMode,
        DateTimeOffset? modified = null,
        CancellationToken ct = default)
    {
        await WriteHeaderAsync(name, content.Length, mode, modified, ct);
        await WriteAsync(content, ct);
        await PadAsync(CpioNewc.PadTo4(content.Length), ct);
    }

    /// <summary>
    /// Streams a file of known length. The length has to be known up front because it
    /// lives in the header, which is written before the data — so a cpio archive cannot
    /// be produced from a source of unknown size without buffering it first.
    /// </summary>
    public async Task WriteFileAsync(
        string name,
        Stream content,
        long size,
        int mode = CpioEntry.RegularFileMode,
        DateTimeOffset? modified = null,
        CancellationToken ct = default)
    {
        await WriteHeaderAsync(name, size, mode, modified, ct);

        var buffer = new byte[81920];
        long copied = 0;

        while (copied < size)
        {
            int wanted = (int)Math.Min(buffer.Length, size - copied);
            int read = await content.ReadAsync(buffer.AsMemory(0, wanted), ct);

            if (read == 0)
                throw new CpioFormatException(
                    $"'{name}' declared {size} bytes but the source ended after {copied}.");

            await WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;
        }

        await PadAsync(CpioNewc.PadTo4(size), ct);
    }

    public Task WriteDirectoryAsync(string name, CancellationToken ct = default) =>
        WriteFileAsync(name, ReadOnlyMemory<byte>.Empty, CpioEntry.DirectoryMode, null, ct);

    /// <summary>Writes the TRAILER!!! member. An archive without it is truncated.</summary>
    public async Task CompleteAsync(CancellationToken ct = default)
    {
        if (_completed) return;

        await WriteHeaderAsync(CpioNewc.TrailerName, 0, 0, DateTimeOffset.UnixEpoch, ct, trailer: true);
        _completed = true;
        await output.FlushAsync(ct);
    }

    private async Task WriteHeaderAsync(
        string name,
        long size,
        int mode,
        DateTimeOffset? modified,
        CancellationToken ct,
        bool trailer = false)
    {
        if (_completed) throw new InvalidOperationException("Archive already completed.");
        if (name.Contains('\0')) throw new CpioFormatException("Entry name contains a NUL.");

        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        int nameSize = nameBytes.Length + 1; // the format counts the terminator

        var header = new StringBuilder(CpioNewc.HeaderLength);
        header.Append(CpioNewc.Magic);
        Field(header, trailer ? 0 : _nextInode++);
        Field(header, mode);
        Field(header, 0); // uid
        Field(header, 0); // gid
        Field(header, 1); // nlink
        Field(header, modified?.ToUnixTimeSeconds() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Field(header, size);
        Field(header, 0); // devmajor
        Field(header, 0); // devminor
        Field(header, 0); // rdevmajor
        Field(header, 0); // rdevminor
        Field(header, nameSize);
        Field(header, 0); // check: always zero for newc, which is what the "no checksum" means

        await WriteAsync(Encoding.ASCII.GetBytes(header.ToString()), ct);
        await WriteAsync(nameBytes, ct);
        await WriteAsync(new byte[] { 0 }, ct);

        await PadAsync(CpioNewc.PadTo4(CpioNewc.HeaderLength + nameSize), ct);
    }

    private static void Field(StringBuilder header, long value) =>
        header.Append(value.ToString("x8", CultureInfo.InvariantCulture));

    private async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await output.WriteAsync(bytes, ct);
        _position += bytes.Length;
    }

    private async ValueTask PadAsync(int count, CancellationToken ct)
    {
        if (count > 0) await WriteAsync(Padding.AsMemory(0, count), ct);
    }

    public async ValueTask DisposeAsync() => await CompleteAsync();
}

public sealed class CpioReader(Stream input)
{
    private long _entryRemaining;
    private long _entrySize;

    /// <summary>
    /// Advances to the next member, returning null at the trailer. Any unread content
    /// from the previous member is skipped, so a caller may ignore entries it does not
    /// want without corrupting the stream position.
    /// </summary>
    public async Task<CpioEntry?> ReadNextAsync(CancellationToken ct = default)
    {
        await SkipAsync(_entryRemaining + CpioNewc.PadTo4(_entrySize), ct);
        _entryRemaining = 0;
        _entrySize = 0;

        var header = new byte[CpioNewc.HeaderLength];
        int read = await ReadUpToAsync(header, ct);

        if (read == 0) throw new CpioFormatException("Archive ended without a TRAILER!!! member.");
        if (read < header.Length) throw new CpioFormatException("Truncated header.");

        string text = Encoding.ASCII.GetString(header);
        if (!text.StartsWith(CpioNewc.Magic, StringComparison.Ordinal))
            throw new CpioFormatException($"Bad magic '{text[..Math.Min(6, text.Length)]}'; expected {CpioNewc.Magic}.");

        int mode = (int)Field(text, 1);
        long mtime = Field(text, 5);
        long size = Field(text, 6);
        long nameSize = Field(text, 11);

        if (nameSize is < 1 or > 4096)
            throw new CpioFormatException($"Implausible name length {nameSize}.");
        if (size < 0)
            throw new CpioFormatException($"Negative file size {size}.");

        var nameBytes = new byte[nameSize];
        await ReadExactlyAsync(nameBytes, ct);

        if (nameBytes[^1] != 0)
            throw new CpioFormatException("Entry name is not NUL-terminated.");

        string name = Encoding.UTF8.GetString(nameBytes, 0, nameBytes.Length - 1);

        await SkipAsync(CpioNewc.PadTo4(CpioNewc.HeaderLength + nameSize), ct);

        if (name == CpioNewc.TrailerName) return null;

        _entrySize = size;
        _entryRemaining = size;

        return new CpioEntry(name, mode, size, mtime);
    }

    public async Task<byte[]> ReadContentAsync(CancellationToken ct = default)
    {
        var content = new byte[_entryRemaining];
        await ReadExactlyAsync(content, ct);
        _entryRemaining = 0;
        return content;
    }

    private static long Field(string header, int index)
    {
        ReadOnlySpan<char> field = header.AsSpan(6 + index * 8, 8);

        return long.TryParse(field, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value)
            ? value
            : throw new CpioFormatException($"Field {index} is not hexadecimal: '{field}'.");
    }

    private async Task<int> ReadUpToAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int filled = 0;
        while (filled < buffer.Length)
        {
            int read = await input.ReadAsync(buffer[filled..], ct);
            if (read == 0) break;
            filled += read;
        }

        return filled;
    }

    private async Task ReadExactlyAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (await ReadUpToAsync(buffer, ct) != buffer.Length)
            throw new CpioFormatException("Archive truncated.");
    }

    private async Task SkipAsync(long count, CancellationToken ct)
    {
        if (count <= 0) return;

        var scratch = new byte[Math.Min(count, 81920)];
        long skipped = 0;

        while (skipped < count)
        {
            int wanted = (int)Math.Min(scratch.Length, count - skipped);
            int read = await input.ReadAsync(scratch.AsMemory(0, wanted), ct);
            if (read == 0) throw new CpioFormatException("Archive truncated while skipping.");
            skipped += read;
        }
    }
}
