using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace WinDrop.Protocol.Dns;

public sealed class DnsFormatException(string message) : Exception(message);

public enum DnsRecordType : ushort
{
    A = 1,
    Ptr = 12,
    Txt = 16,
    Aaaa = 28,
    Srv = 33,
    Any = 255,
}

public sealed record DnsQuestion(string Name, DnsRecordType Type, bool UnicastResponse = false);

public abstract record DnsRecord(string Name, DnsRecordType Type, uint Ttl, bool CacheFlush = false);

public sealed record PtrRecord(string Name, string Target, uint Ttl = 120, bool CacheFlush = false)
    : DnsRecord(Name, DnsRecordType.Ptr, Ttl, CacheFlush);

public sealed record SrvRecord(string Name, string Target, ushort Port, ushort Priority = 0, ushort Weight = 0, uint Ttl = 120, bool CacheFlush = true)
    : DnsRecord(Name, DnsRecordType.Srv, Ttl, CacheFlush);

public sealed record TxtRecord(string Name, IReadOnlyDictionary<string, string> Entries, uint Ttl = 120, bool CacheFlush = true)
    : DnsRecord(Name, DnsRecordType.Txt, Ttl, CacheFlush);

public sealed record AddressRecord(string Name, IPAddress Address, uint Ttl = 120, bool CacheFlush = true)
    : DnsRecord(Name, Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? DnsRecordType.Aaaa : DnsRecordType.A, Ttl, CacheFlush);

/// <summary>A record type we do not model, kept so a message round-trips without loss.</summary>
public sealed record OpaqueRecord(string Name, DnsRecordType Type, byte[] Data, uint Ttl = 120, bool CacheFlush = false)
    : DnsRecord(Name, Type, Ttl, CacheFlush);

/// <summary>
/// DNS wire format, enough of it for multicast DNS service discovery.
///
/// Two things make mDNS differ from unicast DNS in ways that matter here:
///
///   * The top bit of the CLASS field is overloaded. In a question it asks for a
///     unicast reply; in a resource record it is the cache-flush bit, telling peers to
///     discard other records they hold for that name. Masking it off before comparing
///     the class to IN is not optional — forget it and every record looks like class
///     0x8001 and gets rejected.
///
///   * Name compression pointers can point anywhere earlier in the message, including
///     into another pointer. A parser that follows them without a budget can be sent
///     into an infinite loop by two pointers aimed at each other, which is a
///     denial-of-service in three bytes.
/// </summary>
public sealed class DnsMessage
{
    private const int MaxNameLength = 255;
    private const int MaxPointerHops = 64;
    private const ushort ClassIn = 1;
    private const ushort ClassMask = 0x7FFF;
    private const ushort TopBit = 0x8000;

    public ushort Id { get; init; }
    public bool IsResponse { get; init; }
    public bool IsAuthoritative { get; init; } = true;
    public List<DnsQuestion> Questions { get; init; } = [];
    public List<DnsRecord> Answers { get; init; } = [];
    public List<DnsRecord> Additionals { get; init; } = [];

    // ---- writing -----------------------------------------------------------

    public byte[] ToBytes()
    {
        var buffer = new MemoryStream();
        Span<byte> header = stackalloc byte[12];

        BinaryPrimitives.WriteUInt16BigEndian(header[..2], Id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..4], (ushort)((IsResponse ? 0x8000 : 0) | (IsAuthoritative && IsResponse ? 0x0400 : 0)));
        BinaryPrimitives.WriteUInt16BigEndian(header[4..6], (ushort)Questions.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..8], (ushort)Answers.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..10], 0);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..12], (ushort)Additionals.Count);
        buffer.Write(header);

        foreach (DnsQuestion question in Questions)
        {
            WriteName(buffer, question.Name);
            WriteUInt16(buffer, (ushort)question.Type);
            WriteUInt16(buffer, (ushort)(ClassIn | (question.UnicastResponse ? TopBit : 0)));
        }

        foreach (DnsRecord record in Answers.Concat(Additionals))
            WriteRecord(buffer, record);

        return buffer.ToArray();
    }

    private static void WriteRecord(MemoryStream buffer, DnsRecord record)
    {
        WriteName(buffer, record.Name);
        WriteUInt16(buffer, (ushort)record.Type);
        WriteUInt16(buffer, (ushort)(ClassIn | (record.CacheFlush ? TopBit : 0)));

        Span<byte> ttl = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(ttl, record.Ttl);
        buffer.Write(ttl);

        // Length is only known after the data is built, so build it separately. We do
        // not emit compression pointers: they save bytes but every extra pointer is
        // another chance to write an offset that is subtly wrong.
        var data = new MemoryStream();

        switch (record)
        {
            case PtrRecord ptr:
                WriteName(data, ptr.Target);
                break;

            case SrvRecord srv:
                WriteUInt16(data, srv.Priority);
                WriteUInt16(data, srv.Weight);
                WriteUInt16(data, srv.Port);
                WriteName(data, srv.Target);
                break;

            case TxtRecord txt:
                WriteTxt(data, txt.Entries);
                break;

            case AddressRecord address:
                data.Write(address.Address.GetAddressBytes());
                break;

            case OpaqueRecord opaque:
                data.Write(opaque.Data);
                break;

            default:
                throw new DnsFormatException($"Cannot serialise {record.GetType().Name}.");
        }

        WriteUInt16(buffer, (ushort)data.Length);
        data.Position = 0;
        data.CopyTo(buffer);
    }

    private static void WriteTxt(MemoryStream data, IReadOnlyDictionary<string, string> entries)
    {
        // TXT is a sequence of length-prefixed strings, each at most 255 bytes. An empty
        // TXT record still needs one zero byte, or readers treat the record as malformed.
        if (entries.Count == 0)
        {
            data.WriteByte(0);
            return;
        }

        foreach (var (key, value) in entries)
        {
            byte[] bytes = Encoding.UTF8.GetBytes($"{key}={value}");

            if (bytes.Length > 255)
                throw new DnsFormatException($"TXT entry '{key}' is {bytes.Length} bytes; the limit is 255.");

            data.WriteByte((byte)bytes.Length);
            data.Write(bytes);
        }
    }

    private static void WriteName(MemoryStream buffer, string name)
    {
        foreach (string label in name.TrimEnd('.').Split('.'))
        {
            if (label.Length == 0) continue;

            byte[] bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length > 63)
                throw new DnsFormatException($"Label '{label}' is {bytes.Length} bytes; the limit is 63.");

            buffer.WriteByte((byte)bytes.Length);
            buffer.Write(bytes);
        }

        buffer.WriteByte(0);
    }

    private static void WriteUInt16(MemoryStream buffer, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        buffer.Write(bytes);
    }

    // ---- reading -----------------------------------------------------------

    public static DnsMessage Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) throw new DnsFormatException($"Message is {data.Length} bytes; a header is 12.");

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..4]);
        int questionCount = BinaryPrimitives.ReadUInt16BigEndian(data[4..6]);
        int answerCount = BinaryPrimitives.ReadUInt16BigEndian(data[6..8]);
        int authorityCount = BinaryPrimitives.ReadUInt16BigEndian(data[8..10]);
        int additionalCount = BinaryPrimitives.ReadUInt16BigEndian(data[10..12]);

        var message = new DnsMessage
        {
            Id = BinaryPrimitives.ReadUInt16BigEndian(data[..2]),
            IsResponse = (flags & 0x8000) != 0,
            IsAuthoritative = (flags & 0x0400) != 0,
        };

        int offset = 12;

        for (int i = 0; i < questionCount; i++)
        {
            string name = ReadName(data, ref offset);
            var type = (DnsRecordType)ReadUInt16(data, ref offset);
            ushort qclass = ReadUInt16(data, ref offset);

            message.Questions.Add(new DnsQuestion(name, type, (qclass & TopBit) != 0));
        }

        for (int i = 0; i < answerCount; i++)
            message.Answers.Add(ReadRecord(data, ref offset));

        // Authority records are parsed only to keep the offset correct for what follows.
        for (int i = 0; i < authorityCount; i++)
            ReadRecord(data, ref offset);

        for (int i = 0; i < additionalCount; i++)
            message.Additionals.Add(ReadRecord(data, ref offset));

        return message;
    }

    private static DnsRecord ReadRecord(ReadOnlySpan<byte> data, ref int offset)
    {
        string name = ReadName(data, ref offset);
        var type = (DnsRecordType)ReadUInt16(data, ref offset);
        ushort rclass = ReadUInt16(data, ref offset);

        Require(data, offset, 6);
        uint ttl = BinaryPrimitives.ReadUInt32BigEndian(data[offset..(offset + 4)]);
        offset += 4;

        int length = ReadUInt16(data, ref offset);
        Require(data, offset, length);

        int dataStart = offset;
        int dataEnd = offset + length;
        bool cacheFlush = (rclass & TopBit) != 0;

        DnsRecord record = type switch
        {
            DnsRecordType.Ptr => new PtrRecord(name, ReadNameAt(data, dataStart), ttl, cacheFlush),
            DnsRecordType.Srv => ReadSrv(data, name, dataStart, ttl, cacheFlush),
            DnsRecordType.Txt => new TxtRecord(name, ReadTxt(data[dataStart..dataEnd]), ttl, cacheFlush),
            DnsRecordType.A when length == 4 => new AddressRecord(name, new IPAddress(data[dataStart..dataEnd]), ttl, cacheFlush),
            DnsRecordType.Aaaa when length == 16 => new AddressRecord(name, new IPAddress(data[dataStart..dataEnd]), ttl, cacheFlush),
            _ => new OpaqueRecord(name, type, data[dataStart..dataEnd].ToArray(), ttl, cacheFlush),
        };

        // Always resume from the declared length rather than from wherever the payload
        // parser stopped: a record whose contents we misread must not desynchronise the
        // rest of the message.
        offset = dataEnd;
        return record;
    }

    private static SrvRecord ReadSrv(ReadOnlySpan<byte> data, string name, int start, uint ttl, bool cacheFlush)
    {
        int cursor = start;
        ushort priority = ReadUInt16(data, ref cursor);
        ushort weight = ReadUInt16(data, ref cursor);
        ushort port = ReadUInt16(data, ref cursor);

        return new SrvRecord(name, ReadName(data, ref cursor), port, priority, weight, ttl, cacheFlush);
    }

    private static Dictionary<string, string> ReadTxt(ReadOnlySpan<byte> data)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int offset = 0;

        while (offset < data.Length)
        {
            int length = data[offset++];
            if (length == 0) continue;
            if (offset + length > data.Length) break;

            string entry = Encoding.UTF8.GetString(data[offset..(offset + length)]);
            offset += length;

            int equals = entry.IndexOf('=');
            if (equals < 0) entries[entry] = "";
            else entries[entry[..equals]] = entry[(equals + 1)..];
        }

        return entries;
    }

    private static string ReadNameAt(ReadOnlySpan<byte> data, int offset)
    {
        int cursor = offset;
        return ReadName(data, ref cursor);
    }

    private static string ReadName(ReadOnlySpan<byte> data, ref int offset)
    {
        var labels = new List<string>();
        int hops = 0;
        int cursor = offset;
        int? resumeAt = null;
        int totalLength = 0;

        while (true)
        {
            Require(data, cursor, 1);
            byte length = data[cursor];

            if (length == 0)
            {
                cursor++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                Require(data, cursor, 2);
                int pointer = ((length & 0x3F) << 8) | data[cursor + 1];

                // The first pointer ends this name in the stream; later ones are hops
                // taken inside the message and must not move the caller's offset.
                resumeAt ??= cursor + 2;

                if (++hops > MaxPointerHops)
                    throw new DnsFormatException("Name compression pointer loop.");
                if (pointer >= data.Length)
                    throw new DnsFormatException($"Compression pointer to {pointer} is past the message.");

                cursor = pointer;
                continue;
            }

            if ((length & 0xC0) != 0)
                throw new DnsFormatException($"Reserved label type 0x{length:X2}.");

            Require(data, cursor + 1, length);
            labels.Add(Encoding.UTF8.GetString(data[(cursor + 1)..(cursor + 1 + length)]));

            totalLength += length + 1;
            if (totalLength > MaxNameLength)
                throw new DnsFormatException($"Name longer than {MaxNameLength} bytes.");

            cursor += length + 1;
        }

        offset = resumeAt ?? cursor;
        return string.Join('.', labels);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, ref int offset)
    {
        Require(data, offset, 2);
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(data[offset..(offset + 2)]);
        offset += 2;
        return value;
    }

    private static void Require(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > data.Length)
            throw new DnsFormatException($"Read of {length} bytes at {offset} runs past the {data.Length} byte message.");
    }
}
