using System.Net;
using WinDrop.Protocol.Dns;

namespace WinDrop.Protocol.Discovery;

/// <summary>
/// Assembles AirDrop peers out of mDNS records that arrive piecemeal.
///
/// A service is described across four record types — PTR names the instance, SRV gives
/// its host and port, TXT carries the capability flags, and A/AAAA resolve the host —
/// and they need not arrive together or in order. This holds the partial state until an
/// instance has enough to be usable, then reports it once.
///
/// Shared by every transport, because the assembly is identical whether the records came
/// off a local multicast socket or were relayed from a bridge. Only the delivery differs.
/// </summary>
public sealed class ServiceAssembler(string transportName)
{
    private sealed class Pending
    {
        public string? Host;
        public ushort Port;
        public AirDropReceiverFlags Flags;
        public IPAddress? Address;
        public int InterfaceIndex;
    }

    private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Instance name we advertise ourselves, so we never report ourselves as a peer.</summary>
    public string? OwnInstance { get; set; }

    /// <summary>
    /// Folds one message in and returns any peers that became complete as a result.
    /// The interface index is carried through because a link-local address is not
    /// routable without it, and the two arrive from different places.
    /// </summary>
    public IReadOnlyList<AirDropPeer> Absorb(DnsMessage message, int interfaceIndex)
    {
        if (!message.IsResponse) return [];

        foreach (DnsRecord record in message.Answers.Concat(message.Additionals))
            AbsorbRecord(record, interfaceIndex);

        return Complete();
    }

    private void AbsorbRecord(DnsRecord record, int interfaceIndex)
    {
        bool underService = record.Name.EndsWith(AirDropServiceRecord.ServiceType, StringComparison.OrdinalIgnoreCase);

        switch (record)
        {
            case PtrRecord ptr when underService:
                Slot(ptr.Target).InterfaceIndex = interfaceIndex;
                break;

            case SrvRecord srv when underService:
            {
                Pending slot = Slot(srv.Name);
                slot.Host = srv.Target;
                slot.Port = srv.Port;
                slot.InterfaceIndex = interfaceIndex;
                break;
            }

            case TxtRecord txt when underService:
            {
                Pending slot = Slot(txt.Name);

                if (txt.Entries.TryGetValue("flags", out string? raw) && int.TryParse(raw, out int flags))
                    slot.Flags = (AirDropReceiverFlags)flags;

                break;
            }

            case AddressRecord address:
            {
                // Address records name a host, not a service instance, so they attach to
                // every pending peer whose SRV pointed at that host.
                foreach (Pending slot in _pending.Values)
                {
                    if (!string.Equals(slot.Host, address.Name, StringComparison.OrdinalIgnoreCase)) continue;

                    // Prefer link-local IPv6, which is what AirDrop actually uses.
                    if (slot.Address is null || (address.Address.IsIPv6LinkLocal && !slot.Address.IsIPv6LinkLocal))
                    {
                        slot.Address = address.Address;
                        slot.InterfaceIndex = interfaceIndex;
                    }
                }

                break;
            }
        }
    }

    private Pending Slot(string instance)
    {
        if (!_pending.TryGetValue(instance, out Pending? slot))
            _pending[instance] = slot = new Pending();

        return slot;
    }

    private List<AirDropPeer> Complete()
    {
        var ready = new List<AirDropPeer>();

        foreach (var (instance, slot) in _pending)
        {
            if (slot.Address is null || slot.Port == 0) continue;
            if (!_reported.Add(instance)) continue;

            string display = instance.EndsWith(AirDropServiceRecord.ServiceType, StringComparison.OrdinalIgnoreCase)
                ? instance[..^(AirDropServiceRecord.ServiceType.Length + 1)]
                : instance;

            // Never report ourselves. Advertising and browsing share a link, so our own
            // announcements come back to us, and offering to send someone their own
            // files is just wrong.
            if (OwnInstance is not null && display.Equals(OwnInstance, StringComparison.OrdinalIgnoreCase))
                continue;

            IPAddress address = slot.Address;

            // A link-local address is meaningless without the interface it belongs to.
            if (address.IsIPv6LinkLocal)
                address = new IPAddress(address.GetAddressBytes(), slot.InterfaceIndex);

            ready.Add(new AirDropPeer(display, new IPEndPoint(address, slot.Port), slot.Flags, transportName));
        }

        return ready;
    }
}
