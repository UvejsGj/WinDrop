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
        string instance = $"{record.InstanceName}.{AirDropServiceRecord.ServiceType}";

        var message = new DnsMessage
        {
            IsResponse = true,
            Answers =
            {
                new PtrRecord(AirDropServiceRecord.ServiceType, instance),
                new SrvRecord(instance, hostName, (ushort)record.Port),
                new TxtRecord(instance, record.ToTxtRecord()),
            },
        };

        foreach (IPAddress address in addresses)
            message.Additionals.Add(new AddressRecord(hostName, address));

        return message;
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
}
