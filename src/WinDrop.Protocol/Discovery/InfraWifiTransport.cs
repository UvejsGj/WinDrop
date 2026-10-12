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
/// AN IPHONE, ONLY OVER AWDL. iOS binds AirDrop's browser to awdl0 and offers no override,
/// so on an ordinary network it will never see this service no matter how correct the
/// records are. That is the constraint recorded in ADR-001. On Linux, OWL creates awdl0 as
/// an ordinary interface, mDNS joins it like any other, and this same class is then what
/// every iPhone transfer so far has run over. Windows has no such interface, which is the
/// reason <see cref="BridgeTransport"/> exists.
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

    /// <summary>
    /// True once mDNS has joined an AWDL interface, which is the only link an iPhone looks
    /// on. False until first used, and on any machine without one: there the records are
    /// correct and the link layer is wrong.
    /// </summary>
    public bool CanReachAppleDevices => ListeningOn.Any(IsAwdl);

    /// <summary>Where mDNS was joined, as "name/family". Empty until first used.</summary>
    public IReadOnlyList<string> ListeningOn => _mdns.InterfaceNames;

    /// <summary>
    /// Whether a joined interface is OWL's. It names the interface awdl0 unless started
    /// with -h, so the prefix is matched, not the whole name.
    /// </summary>
    internal static bool IsAwdl(string joined) => joined.StartsWith("awdl", StringComparison.Ordinal);

    /// <summary>
    /// Set to go on re-sending the announcement in bursts for as long as it is advertised,
    /// on AWDL only. Null, the default, announces twice and then answers questions, which is
    /// right wherever a peer shares the channel. <see cref="AnnouncementBurst"/> says when
    /// it is not.
    /// </summary>
    public AnnouncementBurst? RepeatAnnouncement { get; init; }

    private Task SendOnAwdlAsync(DnsMessage message, CancellationToken ct) =>
        _mdns.SendOnAsync(message, IsAwdl, ct);

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
        // browsing will not send a fresh query just because we arrived. Twice, a second
        // apart, as RFC 6762 (section 8.3) requires: multicast is never acknowledged, and a
        // single announcement is easily lost, over AWDL most of all.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1), ct);
            await _mdns.SendAsync(AirDropRecords.BuildAnnouncement(record, _hostName, LocalAddresses()), ct);
        }

        if (RepeatAnnouncement is not { } burst) return new Advertisement(this, record);

        // Only on AWDL: the bursts are for a radio the phone visits, and an ordinary
        // network heard the two announcements above.
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task repeating = burst.RepeatAsync(
            () => AirDropRecords.BuildAnnouncement(record, _hostName, LocalAddresses()),
            SendOnAwdlAsync,
            stop.Token);

        return new Advertisement(this, record, stop, repeating);
    }

    private sealed class Advertisement(
        InfraWifiTransport owner,
        AirDropServiceRecord record,
        CancellationTokenSource? stopRepeating = null,
        Task? repeating = null) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // Stop answering first, so no query can be answered after the goodbye.
            owner._advertised = null;

            // And stop repeating, and wait for the burst in flight to end, for the same reason.
            if (stopRepeating is not null && repeating is not null)
            {
                await stopRepeating.CancelAsync();
                await repeating;
                stopRepeating.Dispose();
            }

            DnsMessage goodbye = AirDropRecords.BuildGoodbye(record, owner._hostName);

            await AirDropRecords.SendGoodbyeAsync(goodbye, owner._mdns.SendAsync);

            // Where the announcement needed a burst to be heard, so does its withdrawal.
            // Without it the phone goes on listing a receiver that has stopped, beside the
            // next one to start, until the records expire two minutes later.
            if (owner.RepeatAnnouncement is { } burst)
            {
                await AirDropRecords.SendGoodbyeAsync(
                    goodbye, owner.SendOnAwdlAsync, copies: burst.Copies, spacing: burst.Spacing);
            }
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
        AirDropServiceRecord? advertised = _advertised;

        // Browse questions, and questions about this receiver's own records, get the whole
        // set: refreshing and resolving need answers too, or the entry expires on the peer.
        if (AirDropRecords.IsQueryForService(received.Message)
            || (advertised is not null && AirDropRecords.AsksAboutInstance(received.Message, advertised, _hostName)))
        {
            if (advertised is not null)
            {
                _ = _mdns.SendAsync(
                    AirDropRecords.BuildAnnouncement(advertised, _hostName, LocalAddresses()),
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
