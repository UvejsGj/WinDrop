using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

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
/// the reason <see cref="BridgeTransport"/> exists.
/// </summary>
public sealed class InfraWifiTransport : IAirDropTransport
{
    private readonly MulticastDns _mdns = new();
    private readonly Channel<AirDropPeer> _discovered = Channel.CreateUnbounded<AirDropPeer>();
    private readonly ServiceAssembler _assembler = new("infra-wifi");
    private readonly object _gate = new();

    private AirDropServiceRecord? _advertised;
    private readonly string _hostName = $"{Environment.MachineName.ToLowerInvariant()}.local";
    private bool _started;

    public string Name => "infra-wifi";

    /// <summary>See the class remarks: correct records, wrong link layer for an iPhone.</summary>
    public bool CanReachAppleDevices => false;

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
        _assembler.OwnInstance = record.InstanceName;

        // Announce unsolicited as well as answering queries: a peer that is already
        // browsing will not send a fresh query just because we arrived.
        await _mdns.SendAsync(AirDropRecords.BuildAnnouncement(record, _hostName, LocalAddresses()), ct);

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

        // Re-query periodically. mDNS is lossy by design and a peer that appeared after
        // our first query would otherwise never be found.
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _mdns.SendAsync(AirDropRecords.BuildQuery(), ct);
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
        if (AirDropRecords.IsQueryForService(received.Message))
        {
            if (_advertised is { } record)
            {
                _ = _mdns.SendAsync(
                    AirDropRecords.BuildAnnouncement(record, _hostName, LocalAddresses()),
                    CancellationToken.None);
            }

            return;
        }

        lock (_gate)
        {
            foreach (AirDropPeer peer in _assembler.Absorb(received.Message, received.InterfaceIndex))
                _discovered.Writer.TryWrite(peer);
        }
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
