using System.Buffers.Binary;
using System.Collections;
using System.Text;

namespace WinDrop.Protocol.Plist;

/// <summary>
/// Writer for Apple binary property lists ("bplist00").
///
/// Works in two passes, because the format is self-referential: collections hold
/// references whose width depends on how many objects exist, and the offset table's
/// entry width depends on how long the serialised output turns out to be.
///
///   1. Intern — walk the graph, assign every distinct object an index, and resolve
///      each collection's child references while walking. Once the object count is
///      known, the reference width is fixed.
///   2. Emit — serialise objects in index order, recording where each landed. Once the
///      total length is known, the offset width is fixed and the table can be written.
///
/// Scalars are deduplicated by value, which is what Apple's own writer does — a string
/// used as a key in twenty dictionaries is stored once. Collections are not
/// deduplicated: doing so needs structural comparison of arbitrarily deep graphs, which
/// costs more than the bytes it would save on AirDrop-sized payloads.
///
/// Dictionary keys are written in ordinal sort order. The format does not require it,
/// but it makes output deterministic, which is what makes round-trip and differential
/// tests against another implementation meaningful.
/// </summary>
public sealed class BinaryPlistWriter
{
    private const int MaxDepth = 64;
    private static readonly DateTime AppleEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly List<object?> _objects = [];
    private readonly Dictionary<object, int> _scalars = new(new ScalarComparer());
    private readonly Dictionary<int, int[]> _arrayRefs = [];
    private readonly Dictionary<int, (int[] Keys, int[] Values)> _dictRefs = [];
    private readonly HashSet<object> _interning = new(ReferenceEqualityComparer.Instance);
    private int _nullIndex = -1;

    private BinaryPlistWriter() { }

    public static byte[] Write(object? root)
    {
        var writer = new BinaryPlistWriter();
        int rootIndex = writer.Intern(root, depth: 0);
        return writer.Emit(rootIndex);
    }

    // ---- pass 1: interning -------------------------------------------------

    private int Intern(object? value, int depth)
    {
        if (depth > MaxDepth)
            throw new PlistFormatException($"Nesting deeper than {MaxDepth} levels.");

        value = Normalize(value);

        if (value is null)
        {
            if (_nullIndex < 0)
            {
                _nullIndex = _objects.Count;
                _objects.Add(null);
            }
            return _nullIndex;
        }

        if (value is IDictionary<string, object?> dict)
            return InternDictionary(dict, depth);

        if (value is not string and not byte[] and IEnumerable list)
            return InternArray(list, depth);

        if (_scalars.TryGetValue(value, out int existing))
            return existing;

        int index = _objects.Count;
        _objects.Add(value);
        _scalars[value] = index;
        return index;
    }

    private int InternDictionary(IDictionary<string, object?> dict, int depth)
    {
        if (!_interning.Add(dict))
            throw new PlistFormatException("Reference cycle in the object graph.");

        try
        {
            int index = _objects.Count;
            _objects.Add(dict);

            string[] keys = [.. dict.Keys.OrderBy(k => k, StringComparer.Ordinal)];
            var keyRefs = new int[keys.Length];
            var valueRefs = new int[keys.Length];

            for (int i = 0; i < keys.Length; i++)
                keyRefs[i] = Intern(keys[i], depth + 1);

            for (int i = 0; i < keys.Length; i++)
                valueRefs[i] = Intern(dict[keys[i]], depth + 1);

            _dictRefs[index] = (keyRefs, valueRefs);
            return index;
        }
        finally
        {
            _interning.Remove(dict);
        }
    }

    private int InternArray(IEnumerable list, int depth)
    {
        if (!_interning.Add(list))
            throw new PlistFormatException("Reference cycle in the object graph.");

        try
        {
            int index = _objects.Count;
            _objects.Add(list);

            var refs = new List<int>();
            foreach (object? item in list)
                refs.Add(Intern(item, depth + 1));

            _arrayRefs[index] = [.. refs];
            return index;
        }
        finally
        {
            _interning.Remove(list);
        }
    }

    /// <summary>
    /// Collapses the many .NET numeric types onto the two the format actually has, so
    /// that 1, 1L and (short)1 intern to a single object rather than three.
    /// </summary>
    private static object? Normalize(object? value) => value switch
    {
        null => null,
        bool or string or byte[] or double or long or DateTime or PlistUid => value,
        sbyte v => (long)v,
        byte v => (long)v,
        short v => (long)v,
        ushort v => (long)v,
        int v => (long)v,
        uint v => (long)v,
        ulong v when v <= long.MaxValue => (long)v,
        ulong => throw new PlistFormatException("Unsigned 64-bit value exceeds the signed range the format stores."),
        float v => (double)v,
        decimal v => (double)v,
        DateTimeOffset v => v.UtcDateTime,
        _ => value,
    };

    // ---- pass 2: emission --------------------------------------------------

    private byte[] Emit(int rootIndex)
    {
        int objectRefSize = ByteWidth((ulong)_objects.Count);

        var buffer = new MemoryStream();
        buffer.Write("bplist00"u8);

        var offsets = new long[_objects.Count];

        for (int i = 0; i < _objects.Count; i++)
        {
            offsets[i] = buffer.Position;
            EmitObject(buffer, i, objectRefSize);
        }

        long offsetTableOffset = buffer.Position;
        int offsetIntSize = ByteWidth((ulong)offsetTableOffset);

        foreach (long offset in offsets)
            WriteBigEndian(buffer, (ulong)offset, offsetIntSize);

        Span<byte> trailer = stackalloc byte[32];
        trailer.Clear();
        trailer[6] = (byte)offsetIntSize;
        trailer[7] = (byte)objectRefSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer[8..16], (ulong)_objects.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[16..24], (ulong)rootIndex);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[24..32], (ulong)offsetTableOffset);
        buffer.Write(trailer);

        return buffer.ToArray();
    }

    private void EmitObject(MemoryStream s, int index, int refSize)
    {
        object? value = _objects[index];

        switch (value)
        {
            case null:
                s.WriteByte(0x00);
                return;

            case bool b:
                s.WriteByte(b ? (byte)0x09 : (byte)0x08);
                return;

            case long l:
                EmitInteger(s, l);
                return;

            case double d:
                s.WriteByte(0x23);
                Span<byte> real = stackalloc byte[8];
                BinaryPrimitives.WriteInt64BigEndian(real, BitConverter.DoubleToInt64Bits(d));
                s.Write(real);
                return;

            case DateTime dt:
                s.WriteByte(0x33);
                Span<byte> date = stackalloc byte[8];
                double seconds = (dt.ToUniversalTime() - AppleEpoch).TotalSeconds;
                BinaryPrimitives.WriteInt64BigEndian(date, BitConverter.DoubleToInt64Bits(seconds));
                s.Write(date);
                return;

            case byte[] data:
                EmitMarkerAndCount(s, 0x40, data.Length);
                s.Write(data);
                return;

            case string str:
                EmitString(s, str);
                return;

            case PlistUid uid:
            {
                int width = ByteWidth(uid.Value);
                s.WriteByte((byte)(0x80 | (width - 1)));
                WriteBigEndian(s, uid.Value, width);
                return;
            }

            case IDictionary<string, object?>:
            {
                var (keys, values) = _dictRefs[index];
                EmitMarkerAndCount(s, 0xD0, keys.Length);
                foreach (int r in keys) WriteBigEndian(s, (ulong)r, refSize);
                foreach (int r in values) WriteBigEndian(s, (ulong)r, refSize);
                return;
            }

            case IEnumerable:
            {
                int[] refs = _arrayRefs[index];
                EmitMarkerAndCount(s, 0xA0, refs.Length);
                foreach (int r in refs) WriteBigEndian(s, (ulong)r, refSize);
                return;
            }

            default:
                throw new PlistFormatException($"Cannot serialise {value.GetType().Name} to a binary plist.");
        }
    }

    private static void EmitString(MemoryStream s, string str)
    {
        // The format has no UTF-8 string type in bplist00. Anything outside ASCII goes
        // out as UTF-16 big-endian, and its count is in code units, not bytes.
        bool ascii = true;
        foreach (char c in str)
        {
            if (c > 0x7F) { ascii = false; break; }
        }

        if (ascii)
        {
            EmitMarkerAndCount(s, 0x50, str.Length);
            s.Write(Encoding.ASCII.GetBytes(str));
        }
        else
        {
            EmitMarkerAndCount(s, 0x60, str.Length);
            s.Write(Encoding.BigEndianUnicode.GetBytes(str));
        }
    }

    private static void EmitInteger(MemoryStream s, long value)
    {
        // Widths 1, 2 and 4 are unsigned in the format, so any negative value has to be
        // written as a signed 64-bit integer regardless of how small its magnitude is.
        if (value < 0)
        {
            s.WriteByte(0x13);
            Span<byte> wide = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(wide, value);
            s.Write(wide);
            return;
        }

        int width = ByteWidth((ulong)value);
        int exponent = width switch { 1 => 0, 2 => 1, <= 4 => 2, _ => 3 };
        width = 1 << exponent;

        s.WriteByte((byte)(0x10 | exponent));
        WriteBigEndian(s, (ulong)value, width);
    }

    private static void EmitMarkerAndCount(MemoryStream s, byte marker, int count)
    {
        if (count < 0x0F)
        {
            s.WriteByte((byte)(marker | count));
            return;
        }

        // The 0xF low nibble is an escape: the real count follows as an integer object.
        s.WriteByte((byte)(marker | 0x0F));
        EmitInteger(s, count);
    }

    private static void WriteBigEndian(MemoryStream s, ulong value, int width)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        s.Write(bytes[(8 - width)..]);
    }

    private static int ByteWidth(ulong value) => value switch
    {
        <= 0xFF => 1,
        <= 0xFFFF => 2,
        <= 0xFFFFFFFF => 4,
        _ => 8,
    };

    /// <summary>Value equality for scalars, with structural comparison for byte arrays.</summary>
    private sealed class ScalarComparer : IEqualityComparer<object>
    {
        public new bool Equals(object? x, object? y)
        {
            if (x is byte[] bx && y is byte[] by)
                return bx.AsSpan().SequenceEqual(by);

            return x is not null && x.Equals(y);
        }

        public int GetHashCode(object obj)
        {
            if (obj is not byte[] bytes)
                return obj.GetHashCode();

            var hash = new HashCode();
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }
    }
}
