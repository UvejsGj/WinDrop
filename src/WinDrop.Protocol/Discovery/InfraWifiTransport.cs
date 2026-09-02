using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WinDrop.Protocol.Dns;

namespace WinDrop.Protocol.Discovery;

/// <summary>
/// Discovery over ordinary network interfaces, using mDNS the way Bonjour does.
///
/// WHO THIS CAN REACH. Any peer that browses `_airdrop._tcp.local` on a normal
/// interface: another WinDrop instance, opendrop, and a Mac started with
/// `defaults write com.apple.NetworkBrowser BrowseAllInterfaces -bool true`.
///
/// WHO THIS CANNOT REACH. An iPhone. iOS binds AirDrop's browser to awdl0 and offers no
/// override, so it will never see this service no matter how correct the records are.
/// That is not a defect in this class — it is the constraint recorded in ADR-001, and
/// the reason a BridgeTransport exists in the plan.
/// </summary>
public sealed class InfraWifiTransport : IAirDropTransport
{
    private readonly MulticastDns _mdns = new();
    private readonly Channel<AirDropPeer> _discovered = Channel.CreateUnbounded<AirDropPeer>();
    private readonly Dictionary<string, PendingPeer> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private AirDropServiceRecord? _advertised;
    private string _hostName = $"{Environment.MachineName.ToLowerInvariant()}.local";
    private bool _started;

    public string Name => "infra-wifi";

    /// <summary>See the class remarks: correct records, wrong link layer for an iPhone.</summary>
    public bool CanReachAppleDevices => false;

    /// <summary>A service seen in pieces: PTR, SRV, TXT and A/AAAA arrive as separate records.</summary>
    private sealed class PendingPeer
    {
        public string? Host;
        public ushort Port;
        public AirDropReceiverFlags Flags;
        public IPAddress? Address;
        public int InterfaceIndex;
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_started) return;

            _mdns.MessageReceived += OnMessage;
            _mdns.Start();
            _started = true;
        }
    }

    // ---- advertising -------------------------------------------------------

    public async Task<IAsyncDisposable> AdvertiseAsync(AirDropServiceRecord record, CancellationToken ct = default)
    {
        EnsureStarted();
        _advertised = record;

        // Announce unsolicited as well as answering queries: a peer that is already
        // browsing will not send a fresh query just because we arrived.
        await _mdns.SendAsync(BuildAnnouncement(record), ct);

        return new Advertisement(this);
    }

    private sealed class Advertisement(InfraWifiTransport owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            owner._advertised = null;
            return ValueTask.CompletedTask;
        }
    }

    private DnsMessage BuildAnnouncement(AirDropServiceRecord record)
    {
        string instance = $"{record.InstanceName}.{AirDropServiceRecord.ServiceType}";

        var message = new DnsMessage
        {
            IsResponse = true,
            Answers =
            {
                new PtrRecord(AirDropServiceRecord.ServiceType, instance),
                new SrvRecord(instance, _hostName, (ushort)record.Port),
                new TxtRecord(instance, record.ToTxtRecord()),
            },
        };

        foreach (IPAddress address in LocalAddresses())
            message.Additionals.Add(new AddressRecord(_hostName, address));

        return message;
    }

    /// <summary>
    /// Addresses to publish. Prefers interfaces that have a default gateway, which
    /// excludes virtual switches — the WSL adapter, Hyper-V, VM host-only networks —
    /// whose addresses are unreachable from anywhere a real peer could be. A peer that
    /// walks advertised addresses in order would otherwise stall on one of those before
    /// reaching a useful one.
    ///
    /// Falls back to publishing everything when no interface has a gateway. An isolated
    /// link is exactly the situation AirDrop exists for, and advertising nothing would be
    /// worse than advertising too much.
    /// </summary>
    private static IEnumerable<IPAddress> LocalAddresses()
    {
        var routable = new List<IPAddress>();
        var everything = new List<IPAddress>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            IPInterfaceProperties properties = nic.GetIPProperties();

            bool hasGateway = properties.GatewayAddresses.Any(g =>
                g.Address is { } gateway
                && !gateway.Equals(IPAddress.Any)
                && !gateway.Equals(IPAddress.IPv6Any));

            foreach (UnicastIPAddressInformation info in properties.UnicastAddresses)
            {
                // Link-local IPv6 is what AirDrop actually uses, and a routable IPv4 is
                // what makes the Mac BrowseAllInterfaces path work. Both are worth
                // publishing; neither alone covers both peers we can reach.
                if (!info.Address.IsIPv6LinkLocal && info.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                everything.Add(info.Address);
                if (hasGateway) routable.Add(info.Address);
            }
        }

        return routable.Count > 0 ? routable : everything;
    }

    // ---- browsing ----------------------------------------------------------

    public async IAsyncEnumerable<AirDropPeer> BrowseAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureStarted();

        var query = new DnsMessage
        {
            Questions = { new DnsQuestion(AirDropServiceRecord.ServiceType, DnsRecordType.Ptr) },
        };

        // Re-query periodically. mDNS is lossy by design and a peer that appeared after
        // our first query would otherwise never be found.
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _mdns.SendAsync(query, ct);
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }
                catch (OperationCanceledException) { return; }
            }
        }, ct);

        await foreach (AirDropPeer peer in _discovered.Reader.ReadAllAsync(ct))
            yield return peer;
    }

    private void OnMessage(ReceivedDnsMessage received)
    {
        if (!received.Message.IsResponse)
        {
            AnswerQuery(received);
            return;
        }

        lock (_gate)
        {
            foreach (DnsRecord record in received.Message.Answers.Concat(received.Message.Additionals))
                Absorb(record, received.InterfaceIndex);

            PublishComplete();
        }
    }

    private void Absorb(DnsRecord record, int interfaceIndex)
    {
        switch (record)
        {
            case PtrRecord ptr when ptr.Name.EndsWith(AirDropServiceRecord.ServiceType, StringComparison.OrdinalIgnoreCase):
                Slot(ptr.Target).InterfaceIndex = interfaceIndex;
                break;

            case SrvRecord srv when srv.Name.EndsWith(AirDropServiceRecord.ServiceType, StringComparison.OrdinalIgnoreCase):
            {
                PendingPeer slot = Slot(srv.Name);
                slot.Host = srv.Target;
                slot.Port = srv.Port;
                slot.InterfaceIndex = interfaceIndex;
                break;
            }

            case TxtRecord txt when txt.Name.EndsWith(AirDropServiceRecord.ServiceType, StringComparison.OrdinalIgnoreCase):
            {
                PendingPeer slot = Slot(txt.Name);

                if (txt.Entries.TryGetValue("flags", out string? raw) && int.TryParse(raw, out int flags))
                    slot.Flags = (AirDropReceiverFlags)flags;

                break;
            }

            case AddressRecord address:
            {
                // Address records name a host, not a service instance, so attach them to
                // every pending peer whose SRV pointed at that host.
                foreach (PendingPeer slot in _pending.Values)
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

    private PendingPeer Slot(string instance)
    {
        if (!_pending.TryGetValue(instance, out PendingPeer? slot))
            _pending[instance] = slot = new PendingPeer();

        return slot;
    }

    private void PublishComplete()
    {
        foreach (var (instance, slot) in _pending)
        {
            if (slot.Address is null || slot.Port == 0) continue;
            if (!_announced.Add(instance)) continue;

            IPAddress address = slot.Address;

            // A link-local address is meaningless without the interface it belongs to.
            if (address.IsIPv6LinkLocal)
                address = new IPAddress(address.GetAddressBytes(), slot.InterfaceIndex);

            string display = instance.EndsWith(AirDropServiceRecord.ServiceType, StringComparison.OrdinalIgnoreCase)
                ? instance[..^(AirDropServiceRecord.ServiceType.Length + 1)]
                : instance;

            // Do not report ourselves. Advertising and browsing share one socket set, so
            // our own announcements come back to us, and a peer list that offers to send
            // you your own files is just wrong.
            if (_advertised is { } mine
                && display.Equals(mine.InstanceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }


            _discovered.Writer.TryWrite(new AirDropPeer(
                display, new IPEndPoint(address, slot.Port), slot.Flags, Name));
        }
    }

    private void AnswerQuery(ReceivedDnsMessage received)
    {
        if (_advertised is not { } record) return;

        bool asksForUs = received.Message.Questions.Any(q =>
            q.Type is DnsRecordType.Ptr or DnsRecordType.Any
            && q.Name.Equals(AirDropServiceRecord.ServiceType, StringComparison.OrdinalIgnoreCase));

        if (!asksForUs) return;

        _ = _mdns.SendAsync(BuildAnnouncement(record), CancellationToken.None);
    }

    // ---- connections -------------------------------------------------------

    public async Task<Stream> ConnectAsync(AirDropPeer peer, CancellationToken ct = default)
    {
        var client = new TcpClient(peer.EndPoint.AddressFamily);

        try
        {
            await client.ConnectAsync(peer.EndPoint, ct);
            return client.GetStream();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async IAsyncEnumerable<Stream> AcceptAsync(
        int port,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Dual-mode so one listener serves both the IPv6 link-local path a real peer
        // would use and the IPv4 path a test peer on the LAN is likely to use.
        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
        listener.Start();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException) { yield break; }

                yield return client.GetStream();
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    public ValueTask DisposeAsync() => _mdns.DisposeAsync();
}
