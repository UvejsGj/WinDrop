using System.Net;
using WinDrop.Protocol.Discovery;
using WinDrop.Protocol.Dns;
using Xunit;

namespace WinDrop.Protocol.Tests;

public class DnsMessageTests
{
    private static DnsMessage RoundTrip(DnsMessage message) => DnsMessage.Parse(message.ToBytes());

    [Fact]
    public void Question_round_trips()
    {
        var original = new DnsMessage
        {
            Id = 0x1234,
            Questions = { new DnsQuestion(AirDropServiceRecord.ServiceType, DnsRecordType.Ptr) },
        };

        DnsMessage parsed = RoundTrip(original);

        Assert.False(parsed.IsResponse);
        Assert.Single(parsed.Questions);
        Assert.Equal(AirDropServiceRecord.ServiceType, parsed.Questions[0].Name);
        Assert.Equal(DnsRecordType.Ptr, parsed.Questions[0].Type);
    }

    [Fact]
    public void Unicast_response_bit_survives()
    {
        var parsed = RoundTrip(new DnsMessage
        {
            Questions = { new DnsQuestion("_airdrop._tcp.local", DnsRecordType.Ptr, UnicastResponse: true) },
        });

        Assert.True(parsed.Questions[0].UnicastResponse);
    }

    [Fact]
    public void Full_service_announcement_round_trips()
    {
        const string instance = "WinDrop-abc123._airdrop._tcp.local";
        const string host = "windrop-abc123.local";

        var original = new DnsMessage
        {
            IsResponse = true,
            Answers =
            {
                new PtrRecord(AirDropServiceRecord.ServiceType, instance),
                new SrvRecord(instance, host, 8770),
                new TxtRecord(instance, new Dictionary<string, string> { ["flags"] = "1027" }),
                new AddressRecord(host, IPAddress.Parse("fe80::1c2d:3e4f:5a6b:7c8d")),
            },
        };

        DnsMessage parsed = RoundTrip(original);

        Assert.True(parsed.IsResponse);
        Assert.True(parsed.IsAuthoritative);
        Assert.Equal(4, parsed.Answers.Count);

        var ptr = Assert.IsType<PtrRecord>(parsed.Answers[0]);
        Assert.Equal(instance, ptr.Target);

        var srv = Assert.IsType<SrvRecord>(parsed.Answers[1]);
        Assert.Equal(host, srv.Target);
        Assert.Equal(8770, srv.Port);

        var txt = Assert.IsType<TxtRecord>(parsed.Answers[2]);
        Assert.Equal("1027", txt.Entries["flags"]);

        var address = Assert.IsType<AddressRecord>(parsed.Answers[3]);
        Assert.Equal(IPAddress.Parse("fe80::1c2d:3e4f:5a6b:7c8d"), address.Address);
        Assert.Equal(DnsRecordType.Aaaa, address.Type);
    }

    [Fact]
    public void Cache_flush_bit_is_not_mistaken_for_the_class()
    {
        // The top bit of CLASS is cache-flush in mDNS. A parser that compares the whole
        // field against IN sees 0x8001 and rejects every record Apple sends.
        var parsed = RoundTrip(new DnsMessage
        {
            IsResponse = true,
            Answers = { new SrvRecord("x._airdrop._tcp.local", "host.local", 8770, CacheFlush: true) },
        });

        var srv = Assert.IsType<SrvRecord>(parsed.Answers[0]);
        Assert.True(srv.CacheFlush);
        Assert.Equal(8770, srv.Port);
    }

    [Fact]
    public void Ipv4_addresses_are_written_as_A_records()
    {
        var parsed = RoundTrip(new DnsMessage
        {
            IsResponse = true,
            Answers = { new AddressRecord("host.local", IPAddress.Parse("192.168.1.42")) },
        });

        var address = Assert.IsType<AddressRecord>(parsed.Answers[0]);
        Assert.Equal(DnsRecordType.A, address.Type);
        Assert.Equal(IPAddress.Parse("192.168.1.42"), address.Address);
    }

    [Fact]
    public void Empty_txt_records_survive()
    {
        var parsed = RoundTrip(new DnsMessage
        {
            IsResponse = true,
            Answers = { new TxtRecord("x.local", new Dictionary<string, string>()) },
        });

        Assert.Empty(Assert.IsType<TxtRecord>(parsed.Answers[0]).Entries);
    }

    [Fact]
    public void Additional_records_are_kept_separate_from_answers()
    {
        var parsed = RoundTrip(new DnsMessage
        {
            IsResponse = true,
            Answers = { new PtrRecord("_airdrop._tcp.local", "a._airdrop._tcp.local") },
            Additionals = { new AddressRecord("host.local", IPAddress.Loopback) },
        });

        Assert.Single(parsed.Answers);
        Assert.Single(parsed.Additionals);
    }

    [Fact]
    public void Unicode_instance_names_survive()
    {
        // Bonjour instance names are UTF-8 and Apple devices routinely use apostrophes
        // and non-ASCII in them.
        const string instance = "Uvejs’s iPhone._airdrop._tcp.local";

        var parsed = RoundTrip(new DnsMessage
        {
            IsResponse = true,
            Answers = { new PtrRecord("_airdrop._tcp.local", instance) },
        });

        Assert.Equal(instance, Assert.IsType<PtrRecord>(parsed.Answers[0]).Target);
    }

    [Fact]
    public void Compression_pointers_are_followed()
    {
        // Hand-built: a question for "a.local", then a PTR answer whose name is a
        // pointer back to offset 12. Real responders compress heavily, so a parser that
        // cannot follow a pointer cannot read anything Apple sends.
        var message = new List<byte>
        {
            0x00, 0x01, 0x84, 0x00,
            0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
        };

        message.AddRange([1, (byte)'a', 5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0]);
        message.AddRange([0x00, 0x0C, 0x00, 0x01]);          // QTYPE PTR, QCLASS IN
        message.AddRange([0xC0, 0x0C]);                       // NAME -> pointer to offset 12
        message.AddRange([0x00, 0x0C, 0x00, 0x01]);          // TYPE PTR, CLASS IN
        message.AddRange([0x00, 0x00, 0x00, 0x78]);          // TTL 120
        message.AddRange([0x00, 0x02]);                       // RDLENGTH
        message.AddRange([0xC0, 0x0C]);                       // RDATA -> pointer to offset 12

        DnsMessage parsed = DnsMessage.Parse(message.ToArray());

        Assert.Equal("a.local", parsed.Questions[0].Name);
        Assert.Equal("a.local", parsed.Answers[0].Name);
        Assert.Equal("a.local", Assert.IsType<PtrRecord>(parsed.Answers[0]).Target);
    }

    [Fact]
    public void A_pointer_loop_is_refused_rather_than_hung()
    {
        // Two pointers aimed at each other. Without a hop budget this is an infinite
        // loop reachable by anyone who can send a packet to port 5353.
        var message = new List<byte>
        {
            0x00, 0x01, 0x00, 0x00,
            0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };

        message.AddRange([0xC0, 0x0E]); // offset 12 -> 14
        message.AddRange([0xC0, 0x0C]); // offset 14 -> 12

        Assert.Throws<DnsFormatException>(() => DnsMessage.Parse(message.ToArray()));
    }

    [Fact]
    public void A_pointer_past_the_end_is_refused()
    {
        var message = new List<byte>
        {
            0x00, 0x01, 0x00, 0x00,
            0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xC0, 0xFF,
        };

        Assert.Throws<DnsFormatException>(() => DnsMessage.Parse(message.ToArray()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(11)]
    public void Truncated_headers_are_refused(int length)
    {
        Assert.Throws<DnsFormatException>(() => DnsMessage.Parse(new byte[length]));
    }

    [Fact]
    public void A_record_count_larger_than_the_message_is_refused()
    {
        // Claims 255 answers in a 12-byte message.
        var message = new byte[] { 0, 1, 0x84, 0, 0, 0, 0, 0xFF, 0, 0, 0, 0 };
        Assert.Throws<DnsFormatException>(() => DnsMessage.Parse(message));
    }

    [Fact]
    public void An_unknown_record_type_is_preserved_rather_than_dropped()
    {
        var parsed = RoundTrip(new DnsMessage
        {
            IsResponse = true,
            Answers = { new OpaqueRecord("x.local", (DnsRecordType)99, [1, 2, 3]) },
        });

        var opaque = Assert.IsType<OpaqueRecord>(parsed.Answers[0]);
        Assert.Equal((DnsRecordType)99, opaque.Type);
        Assert.Equal([1, 2, 3], opaque.Data);
    }

    [Fact]
    public void Oversized_labels_are_refused_on_write()
    {
        var message = new DnsMessage { Questions = { new DnsQuestion(new string('a', 64) + ".local", DnsRecordType.Ptr) } };
        Assert.Throws<DnsFormatException>(() => message.ToBytes());
    }

    [Fact]
    public void Oversized_txt_entries_are_refused_on_write()
    {
        var message = new DnsMessage
        {
            IsResponse = true,
            Answers = { new TxtRecord("x.local", new Dictionary<string, string> { ["k"] = new string('v', 300) }) },
        };

        Assert.Throws<DnsFormatException>(() => message.ToBytes());
    }
}

/// <summary>
/// Which questions the receiver answers. Only the browse question used to be answered, so
/// a peer refreshing or resolving this receiver's records met silence, and the records
/// expired on it after their TTL: session 12's laptop vanished from the AirDrop list while
/// both sides were up.
/// </summary>
public class QueryAnsweringTests
{
    private const string Host = "kali.local";
    private const string Instance = "kali-23fb9e._airdrop._tcp.local";

    private static readonly AirDropServiceRecord Record =
        new("kali-23fb9e", AirDropServiceRecord.DefaultPort, AirDropReceiverFlags.SupportsDvZip);

    private static DnsMessage Query(string name, DnsRecordType type) =>
        new() { Questions = { new DnsQuestion(name, type) } };

    [Theory]
    [InlineData(Instance, DnsRecordType.Srv)]
    [InlineData(Instance, DnsRecordType.Txt)]
    [InlineData(Instance, DnsRecordType.Any)]
    [InlineData(Host, DnsRecordType.Aaaa)]
    [InlineData(Host, DnsRecordType.A)]
    [InlineData(Host, DnsRecordType.Any)]
    [InlineData("KALI-23FB9E._airdrop._tcp.local", DnsRecordType.Srv)] // DNS names ignore case
    [InlineData("Kali.Local", DnsRecordType.Aaaa)]
    public void Questions_about_this_receiver_are_answered(string name, DnsRecordType type)
    {
        Assert.True(AirDropRecords.AsksAboutInstance(Query(name, type), Record, Host));
    }

    [Theory]
    [InlineData("kali-69c4c4._airdrop._tcp.local", DnsRecordType.Srv)] // another receiver
    [InlineData("other.local", DnsRecordType.Aaaa)]                    // another host
    [InlineData(Instance, DnsRecordType.Aaaa)]                         // right name, wrong type
    [InlineData(Host, DnsRecordType.Srv)]
    [InlineData(AirDropServiceRecord.ServiceType, DnsRecordType.Ptr)]  // the browse question has its own check
    public void Questions_about_anything_else_are_not(string name, DnsRecordType type)
    {
        Assert.False(AirDropRecords.AsksAboutInstance(Query(name, type), Record, Host));
    }

    [Fact]
    public void A_response_is_never_taken_for_a_question()
    {
        var response = new DnsMessage
        {
            IsResponse = true,
            Questions = { new DnsQuestion(Instance, DnsRecordType.Srv) },
        };

        Assert.False(AirDropRecords.AsksAboutInstance(response, Record, Host));
    }
}

/// <summary>
/// Withdrawing a service when the receiver stops. Without it, session 11's phone listed the
/// stopped receiver beside the live one, both as "kali", and only one could be reached.
/// </summary>
public class GoodbyeTests
{
    private static readonly AirDropServiceRecord Record =
        new("kali-69c4c4", AirDropServiceRecord.DefaultPort, AirDropReceiverFlags.SupportsDvZip);

    [Fact]
    public void A_goodbye_withdraws_exactly_what_was_announced()
    {
        DnsMessage announcement = DnsMessage.Parse(
            AirDropRecords.BuildAnnouncement(Record, "kali.local", [IPAddress.Parse("fe80::1")]).ToBytes());
        DnsMessage goodbye = DnsMessage.Parse(AirDropRecords.BuildGoodbye(Record, "kali.local").ToBytes());

        // The same records, by name and type, so a peer's cache matches and drops them...
        Assert.Equal(
            announcement.Answers.Select(r => (r.Name, r.Type)),
            goodbye.Answers.Select(r => (r.Name, r.Type)));

        // ...with a TTL of zero, which is what makes it a goodbye (RFC 6762, section 10.1).
        Assert.All(goodbye.Answers, r => Assert.Equal(0u, r.Ttl));
        Assert.All(announcement.Answers, r => Assert.Equal(120u, r.Ttl));

        // The host's address is not the service's to withdraw.
        Assert.Empty(goodbye.Additionals);
        Assert.True(goodbye.IsResponse);
    }

    [Fact]
    public async Task A_goodbye_is_sent_twice()
    {
        // Multicast is never acknowledged, and over AWDL one datagram is easily lost.
        var sent = new List<DnsMessage>();
        DnsMessage goodbye = AirDropRecords.BuildGoodbye(Record, "kali.local");

        await AirDropRecords.SendGoodbyeAsync(goodbye, (message, _) =>
        {
            sent.Add(message);
            return Task.CompletedTask;
        });

        Assert.Equal(2, sent.Count);
        Assert.All(sent, message => Assert.Same(goodbye, message));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("disposed")]
    [InlineData("bridge")]
    public async Task A_goodbye_that_cannot_be_sent_does_not_fail_the_shutdown(string failure)
    {
        // It runs while the program is stopping: the socket may be closed, the bridge gone.
        Exception error = failure switch
        {
            "io" => new IOException("connection reset"),
            "disposed" => new ObjectDisposedException("socket"),
            _ => new BridgeException("Control connection is not open."),
        };

        await AirDropRecords.SendGoodbyeAsync(
            AirDropRecords.BuildGoodbye(Record, "kali.local"),
            (_, _) => Task.FromException(error));
    }

    [Fact]
    public async Task A_goodbye_that_hangs_is_cut_off()
    {
        // A send that never completes must not hold the program open on Ctrl+C.
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await AirDropRecords.SendGoodbyeAsync(
            AirDropRecords.BuildGoodbye(Record, "kali.local"),
            (_, ct) => Task.Delay(Timeout.Infinite, ct),
            timeoutPerSend: TimeSpan.FromMilliseconds(100));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"Took {timer.Elapsed}.");
    }
}
