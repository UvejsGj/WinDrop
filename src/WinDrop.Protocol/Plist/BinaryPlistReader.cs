using System.Buffers.Binary;
using System.Text;

namespace WinDrop.Protocol.Plist;

/// <summary>A keyed-archiver UID. A distinct type in the format, not an integer.</summary>
public sealed record PlistUid(ulong Value);

public sealed class PlistFormatException(string message) : Exception(message);

/// <summary>
/// Reader for Apple binary property lists ("bplist00").
///
/// SECURITY POSTURE. An AirDrop receiver parses a bplist sent by an unauthenticated
/// peer — the /Ask body arrives and is decoded *before* the user is shown any consent
/// prompt. Every length, offset and object reference in the file is therefore
/// attacker-controlled. This reader validates all of them against the buffer bounds,
/// caps recursion depth, and detects reference cycles, rather than trusting the
/// structure to be well formed. A malformed plist must fail, not hang or over-read.
///
/// FORMAT. Three regions after the 8-byte header: an object table, an offset table,
/// and a 32-byte trailer at the very end. The trailer is read first because it holds
/// the sizes needed to interpret everything else:
///
///   [0..5)   unused
///   [5]      sort version
///   [6]      offset int size   - width of each entry in the offset table
///   [7]      object ref size   - width of each reference inside collections
///   [8..16)  number of objects
///   [16..24) index of the root object
///   [24..32) file offset of the offset table
///
/// Each object begins with a marker byte: the high nibble is the type, the low nibble
/// is usually a count. A low nibble of 0xF is an escape meaning "the count does not fit
/// in four bits" — an integer object follows, holding the real count.
/// </summary>
public sealed class BinaryPlistReader
{
    private const int MaxDepth = 64;
    private static readonly DateTime AppleEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly byte[] _data;
    private readonly long[] _offsets;
    private readonly int _objectRefSize;

    private BinaryPlistReader(byte[] data, long[] offsets, int objectRefSize)
    {
        _data = data;
        _offsets = offsets;
        _objectRefSize = objectRefSize;
    }

    public static object? Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        // 8-byte header + at least one object + 32-byte trailer.
        if (data.Length < 8 + 32)
            throw new PlistFormatException($"Too short to be a binary plist ({data.Length} bytes).");

        if (Encoding.ASCII.GetString(data, 0, 6) != "bplist")
            throw new PlistFormatException("Missing bplist magic.");

        string version = Encoding.ASCII.GetString(data, 6, 2);
        if (version != "00")
            throw new PlistFormatException($"Unsupported binary plist version {version}. Only bplist00 is defined for AirDrop.");

        int t = data.Length - 32;
        int offsetIntSize = data[t + 6];
        int objectRefSize = data[t + 7];
        ulong numObjects = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(t + 8, 8));
        ulong topObject = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(t + 16, 8));
        ulong offsetTableOffset = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(t + 24, 8));

        if (offsetIntSize is < 1 or > 8)
            throw new PlistFormatException($"Invalid offset int size {offsetIntSize}.");
        if (objectRefSize is < 1 or > 8)
            throw new PlistFormatException($"Invalid object ref size {objectRefSize}.");
        if (numObjects == 0)
            throw new PlistFormatException("Object table is empty.");

        // Reject an object count that could not possibly be backed by this file before
        // allocating anything sized from it.
        if (numObjects > (ulong)data.Length)
            throw new PlistFormatException($"Object count {numObjects} exceeds file length.");
        if (topObject >= numObjects)
            throw new PlistFormatException($"Root object index {topObject} is out of range.");

        ulong tableEnd = offsetTableOffset + numObjects * (ulong)offsetIntSize;
        if (offsetTableOffset < 8 || tableEnd > (ulong)t)
            throw new PlistFormatException("Offset table lies outside the file.");

        var offsets = new long[numObjects];
        for (ulong i = 0; i < numObjects; i++)
        {
            long offset = (long)ReadBigEndian(data, (int)(offsetTableOffset + i * (ulong)offsetIntSize), offsetIntSize);

            if (offset < 8 || offset >= t)
                throw new PlistFormatException($"Object {i} has offset {offset}, outside the object region.");

            offsets[i] = offset;
        }

        var reader = new BinaryPlistReader(data, offsets, objectRefSize);
        return reader.ReadObject((long)topObject, depth: 0, new HashSet<long>());
    }

    private object? ReadObject(long index, int depth, HashSet<long> visiting)
    {
        if (depth > MaxDepth)
            throw new PlistFormatException($"Nesting deeper than {MaxDepth} levels.");
        if (index < 0 || index >= _offsets.Length)
            throw new PlistFormatException($"Object reference {index} is out of range.");

        int pos = (int)_offsets[index];
        byte marker = _data[pos++];
        int type = marker >> 4;
        int low = marker & 0x0F;

        switch (type)
        {
            case 0x0:
                return low switch
                {
                    0x0 => null,
                    0x8 => false,
                    0x9 => true,
                    0xF => null, // fill byte; carries no value
                    _ => throw new PlistFormatException($"Unknown singleton marker 0x{marker:X2}."),
                };

            case 0x1: return ReadInteger(pos, 1 << low);
            case 0x2: return ReadReal(pos, 1 << low);

            case 0x3:
                if (low != 0x3) throw new PlistFormatException($"Unknown date marker 0x{marker:X2}.");
                Require(pos, 8);
                return AppleEpoch.AddSeconds(BitConverter.Int64BitsToDouble(
                    BinaryPrimitives.ReadInt64BigEndian(_data.AsSpan(pos, 8))));

            case 0x4:
            {
                long count = ReadCount(ref pos, low);
                Require(pos, count);
                return _data.AsSpan(pos, (int)count).ToArray();
            }

            case 0x5:
            {
                long count = ReadCount(ref pos, low);
                Require(pos, count);
                return Encoding.ASCII.GetString(_data, pos, (int)count);
            }

            case 0x6:
            {
                // The count is in UTF-16 code units, not bytes.
                long count = ReadCount(ref pos, low);
                Require(pos, count * 2);
                return Encoding.BigEndianUnicode.GetString(_data, pos, (int)count * 2);
            }

            case 0x8:
            {
                int width = low + 1;
                Require(pos, width);
                return new PlistUid(ReadBigEndian(_data, pos, width));
            }

            // Only collections can form a cycle, so the guard is scoped to them: a
            // scalar referenced twice from one container is deduplication, not a loop.
            case 0xA:
            case 0xC:
            {
                long count = ReadCount(ref pos, low);
                return ReadCollection(index, pos, count, depth, visiting);
            }

            case 0xD:
            {
                long count = ReadCount(ref pos, low);
                return ReadDictionary(index, pos, count, depth, visiting);
            }

            default:
                throw new PlistFormatException($"Unknown object marker 0x{marker:X2}.");
        }
    }

    private object ReadCollection(long index, int pos, long count, int depth, HashSet<long> visiting)
    {
        if (!visiting.Add(index))
            throw new PlistFormatException($"Reference cycle through object {index}.");

        try
        {
            Require(pos, count * _objectRefSize);

            var items = new List<object?>((int)count);
            for (long i = 0; i < count; i++)
            {
                long r = (long)ReadBigEndian(_data, pos + (int)(i * _objectRefSize), _objectRefSize);
                items.Add(ReadObject(r, depth + 1, visiting));
            }

            return items;
        }
        finally
        {
            visiting.Remove(index);
        }
    }

    private object ReadDictionary(long index, int pos, long count, int depth, HashSet<long> visiting)
    {
        if (!visiting.Add(index))
            throw new PlistFormatException($"Reference cycle through object {index}.");

        try
        {
            // Keys come first as a contiguous run of refs, then the values.
            Require(pos, count * 2 * _objectRefSize);

            var dict = new Dictionary<string, object?>((int)count, StringComparer.Ordinal);

            for (long i = 0; i < count; i++)
            {
                long keyRef = (long)ReadBigEndian(_data, pos + (int)(i * _objectRefSize), _objectRefSize);
                long valRef = (long)ReadBigEndian(_data, pos + (int)((count + i) * _objectRefSize), _objectRefSize);

                if (ReadObject(keyRef, depth + 1, visiting) is not string key)
                    throw new PlistFormatException($"Dictionary key {i} is not a string.");

                dict[key] = ReadObject(valRef, depth + 1, visiting);
            }

            return dict;
        }
        finally
        {
            visiting.Remove(index);
        }
    }

    /// <summary>
    /// Resolves a marker's low nibble into a count, following the 0xF escape to an
    /// integer object when the count does not fit in four bits.
    /// </summary>
    private long ReadCount(ref int pos, int low)
    {
        if (low != 0xF)
            return low;

        Require(pos, 1);
        byte sizeMarker = _data[pos++];

        if ((sizeMarker >> 4) != 0x1)
            throw new PlistFormatException($"Count escape expected an integer, found marker 0x{sizeMarker:X2}.");

        int width = 1 << (sizeMarker & 0x0F);
        Require(pos, width);

        ulong count = ReadBigEndian(_data, pos, width);
        pos += width;

        if (count > int.MaxValue)
            throw new PlistFormatException($"Count {count} is implausibly large.");

        return (long)count;
    }

    private object ReadInteger(int pos, int width)
    {
        Require(pos, width);

        // 1, 2 and 4 byte integers are unsigned; 8-byte integers are signed. That
        // asymmetry is in the format itself, not a quirk of this reader.
        return width switch
        {
            1 => (long)_data[pos],
            2 => (long)BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(pos, 2)),
            4 => (long)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(pos, 4)),
            8 => BinaryPrimitives.ReadInt64BigEndian(_data.AsSpan(pos, 8)),
            16 => ReadInt128(pos),
            _ => throw new PlistFormatException($"Unsupported integer width {width}."),
        };
    }

    private object ReadInt128(int pos)
    {
        // 128-bit integers are legal but do not occur in AirDrop payloads. Narrow when
        // the value fits so callers see a long; refuse otherwise rather than truncating.
        Require(pos, 16);

        long high = BinaryPrimitives.ReadInt64BigEndian(_data.AsSpan(pos, 8));
        ulong lowBits = BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(pos + 8, 8));

        if (high == 0 && lowBits <= long.MaxValue) return (long)lowBits;
        if (high == -1 && lowBits >= unchecked((ulong)long.MinValue)) return unchecked((long)lowBits);

        throw new PlistFormatException("128-bit integer does not fit in Int64.");
    }

    private object ReadReal(int pos, int width)
    {
        Require(pos, width);

        return width switch
        {
            4 => (double)BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(pos, 4))),
            8 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(_data.AsSpan(pos, 8))),
            _ => throw new PlistFormatException($"Unsupported real width {width}."),
        };
    }

    private void Require(int pos, long length)
    {
        if (pos < 0 || length < 0 || pos + length > _data.Length - 32)
            throw new PlistFormatException($"Object at {pos} claims {length} bytes, which runs past the object region.");
    }

    private static ulong ReadBigEndian(byte[] data, int pos, int width)
    {
        if (pos < 0 || pos + width > data.Length)
            throw new PlistFormatException($"Read of {width} bytes at {pos} runs past end of file.");

        ulong value = 0;
        for (int i = 0; i < width; i++)
            value = (value << 8) | data[pos + i];

        return value;
    }
}
