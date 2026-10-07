using System.Net;
using WinDrop.Protocol.Dns;

namespace WinDrop.Protocol.Discovery;

/// <summary>
/// Builds the mDNS messages an AirDrop endpoint sends. Shared by every transport: the
/// records are identical whether they go out on a local multicast socket or are relayed
/// to a bridge for transmission on awdl0. Only who puts them on the wire differs.
/// </summary>
public static class AirDropRecords
{
    public static DnsMessage BuildAnnouncement(
        AirDropServiceRecord record,
        string hostName,
        IEnumerable<IPAddress> addresses)
    {
        DnsMessage message = ServiceRecords(record, hostName, ttl: 120);

        foreach (IPAddress address in addresses)
            message.Additionals.Add(new AddressRecord(hostName, address));

        return message;
    }

    /// <summary>
    /// The same service records with a TTL of zero, which is how RFC 6762 (section 10.1)
    /// withdraws them: a peer that hears it drops the service from its cache at once.
    ///
    /// Without it, a receiver that stops just falls silent, and peers keep showing it until
    /// the records expire. Each start picks a new random instance name, so a restart then
    /// leaves the old entry beside the new one. Session 11's phone listed two receivers, both
    /// named "kali", and only one of them was alive.
    ///
    /// The address records are left alone: they belong to the host, not the service, and on
    /// a bridge the address is the bridge's.
    /// </summary>
    public static DnsMessage BuildGoodbye(AirDropServiceRecord record, string hostName) =>
        ServiceRecords(record, hostName, ttl: 0);

    /// <summary>
    /// Sends a goodbye twice, a moment apart, and never lets it fail or stall a shutdown.
    /// Twice because multicast is never acknowledged, and over AWDL one datagram is easily
    /// lost; RFC 6762 repeats announcements for the same reason. Each send is time-boxed,
    /// since this runs while the program is stopping, often after its own token has been
    /// cancelled. A goodbye that cannot be sent costs only what was there before it existed:
    /// the entry lingers until it expires.
    /// </summary>
    internal static async Task SendGoodbyeAsync(
        DnsMessage goodbye,
        Func<DnsMessage, CancellationToken, Task> send,
        TimeSpan? timeoutPerSend = null)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromMilliseconds(250));

            using var timeout = new CancellationTokenSource(timeoutPerSend ?? TimeSpan.FromSeconds(2));

            try
            {
                await send(goodbye, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException
                or ObjectDisposedException or OperationCanceledException or BridgeException)
            {
                return;
            }
        }
    }

    private static DnsMessage ServiceRecords(AirDropServiceRecord record, string hostName, uint ttl)
    {
        string instance = $"{record.InstanceName}.{AirDropServiceRecord.ServiceType}";

        return new DnsMessage
        {
            IsResponse = true,
            Answers =
            {
                new PtrRecord(AirDropServiceRecord.ServiceType, instance, ttl),
                new SrvRecord(instance, hostName, (ushort)record.Port, Ttl: ttl),
                new TxtRecord(instance, record.ToTxtRecord(), ttl),
            },
        };
    }

    public static DnsMessage BuildQuery() => new()
    {
        Questions = { new DnsQuestion(AirDropServiceRecord.ServiceType, DnsRecordType.Ptr) },
    };

    /// <summary>True if this message is a browse query we should answer.</summary>
    public static bool IsQueryForService(DnsMessage message) =>
        !message.IsResponse
        && message.Questions.Any(q =>
            q.Type is DnsRecordType.Ptr or DnsRecordType.Any
            && q.Name.Equals(AirDropServiceRecord.ServiceType, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True if a query asks for one of this receiver's own records: its SRV or TXT by
    /// instance name, or its address by host name.
    ///
    /// A browse question for the service type is not the only one a peer asks. Having found
    /// a receiver, it resolves it with questions about that instance and its host, and it
    /// keeps the cached records alive by asking for each again before its TTL runs out
    /// (RFC 6762, section 5.2). Answering only the browse question, as this code first did,
    /// lets those records expire after 120 seconds although the receiver is up and healthy.
    /// Session 12 saw exactly that shape: the phone still an active AWDL peer, the receiver
    /// running, and the laptop gone from the AirDrop list.
    /// </summary>
    public static bool AsksAboutInstance(DnsMessage message, AirDropServiceRecord record, string hostName)
    {
        if (message.IsResponse) return false;

        string instance = $"{record.InstanceName}.{AirDropServiceRecord.ServiceType}";

        return message.Questions.Any(q =>
            (q.Type is DnsRecordType.Srv or DnsRecordType.Txt or DnsRecordType.Any
                && q.Name.Equals(instance, StringComparison.OrdinalIgnoreCase))
            || (q.Type is DnsRecordType.A or DnsRecordType.Aaaa or DnsRecordType.Any
                && q.Name.Equals(hostName, StringComparison.OrdinalIgnoreCase)));
    }
}
